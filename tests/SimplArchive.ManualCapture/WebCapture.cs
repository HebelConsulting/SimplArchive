using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SimplArchive.SelfHosting;

namespace SimplArchive.ManualCapture;

// Captures the web (Blazor workbench) screens: stands up the real app via the shared SelfHostedApp engine (ADR
// 0502), drives the real interactive OIDC login with system Chrome (Playwright), and screenshots each bottom tab at
// a fixed viewport for consistent framing. Heavy (Testcontainers + Chrome, like the UI-E2E suite), so it runs on
// `main` only — the PR gate uses the cheap desktop capture.
public static partial class WebCapture
{
    // A fixed, generous 16:10 viewport so every web screenshot has identical dimensions in the manual.
    // Wide enough that all 13 bottom tabs fit without horizontal overflow — otherwise clicking a right-side tab
    // (Tags/Tenant) makes the browser scroll the workbench right to bring it into view, clipping the left panes.
    private static readonly int ViewportWidth = 1680;
    private static readonly int ViewportHeight = 900;

    public static async Task RunAsync(string outDir)
    {
        await using var app = new SelfHostedApp();
        Console.WriteLine("[web] booting the self-hosted app (Postgres + SeaweedFS + OpenSearch + Tika + Gotenberg + API)…");
        await app.StartAsync();
        Console.WriteLine($"[web] app ready at {app.BaseUrl}");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
            ColorScheme = ColorScheme.Light,
        });
        // Pre-dismiss the one-time post-logon desktop-client promo (ADR 0505): on a fresh context its modal
        // would overlay the workbench and its scrim intercepts pointer events, breaking every post-login capture.
        await context.AddInitScriptAsync("try { localStorage.setItem('sa.desktopClientNoticeDismissed', '1'); } catch (e) { }");

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(60000);

        // Land on the SPA (DOMContentLoaded, not NetworkIdle — a Blazor WASM SPA never goes network-idle).
        await page.GotoAsync(app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByText("SimplArchive").First.WaitForAsync();

        foreach (var screen in Screens.Web.Where(s => s.BeforeLogin))
        {
            await ShotAsync(page, outDir, screen.Name);
        }

        await LoginAsync(page);

        foreach (var screen in Screens.Web.Where(s => !s.BeforeLogin))
        {
            if (screen.Tab is { } tab)
            {
                Console.WriteLine($"[web] opening tab '{tab}'");
                await page.Locator(".wb-tab").Filter(new() { HasText = tab }).First.ClickAsync();
                // Let the tab's panel render + any first-load fetch settle before the shot.
                await page.WaitForTimeoutAsync(1500);
            }

            await EnrichAsync(page, screen.Name);
            await ShotAsync(page, outDir, screen.Name);
        }

        await CaptureVersionCompareAsync(page, outDir);
    }

    // Opens the "Compare versions" dialog on the two-revision demo document ("Offer 2025-014", ADR 0502) and shots
    // the inline diff — the feature figure for the manual's versioning chapter.
    private static async Task CaptureVersionCompareAsync(IPage page, string outDir)
    {
        try
        {
            await page.Locator(".wb-tab").Filter(new() { HasText = "Repositories" }).First.ClickAsync();
            await page.GetByText("Demo Repository").First.ClickAsync();
            var row = page.Locator(".wb-list-row").Filter(new() { HasText = "Offer 2025-014" });
            await row.First.WaitForAsync(new() { Timeout = 15000 });
            await row.First.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Compare versions" }).ClickAsync();
            var dialog = page.Locator(".mud-dialog");
            await dialog.First.WaitForAsync(new() { Timeout = 10000 });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Compare", Exact = true }).ClickAsync();
            await page.WaitForTimeoutAsync(1800); // let the diff render
            await ShotAsync(page, outDir, "version-compare");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[web] version-compare skipped: {ex.Message}");
        }
    }

    // Screen-specific interactions that populate a tab with real content before the shot, so the web figures aren't
    // empty "nothing selected" states. Driven off the demo seed (ADR 0214): Demo Repository → Invoices → the
    // "Invoice 2025-001" document. Best-effort — a failure here shouldn't abort the whole capture, so each block is
    // guarded (the tab still gets shot in its default state).
    private static async Task EnrichAsync(IPage page, string name)
    {
        try
        {
            switch (name)
            {
                case "repositories":
                    // Drill Demo Repository → Invoices → select the document, so the detail + preview panes fill
                    // (the seeded invoice PDF renders via pdf.js, with the seeded highlight + sticky note on it).
                    await page.GetByText("Demo Repository").First.ClickAsync();
                    var invoices = page.Locator(".wb-list-row").Filter(new() { HasText = "Invoices" });
                    await invoices.First.WaitForAsync(new() { Timeout = 15000 });
                    await invoices.First.DblClickAsync();
                    var doc = page.Locator(".wb-list-row").Filter(new() { HasText = "Invoice 2025-001" });
                    await doc.First.WaitForAsync(new() { Timeout = 15000 });
                    await doc.First.ClickAsync();
                    await page.Locator(".wb-sysfields").First.WaitForAsync(new() { Timeout = 15000 });
                    // Wait for the PDF preview + the annotation markers to render.
                    try { await page.Locator(".wb-pv-note").First.WaitForAsync(new() { Timeout = 8000 }); } catch { /* preview still shown */ }
                    await page.WaitForTimeoutAsync(2000);
                    break;

                case "search":
                    // Prefer a distinctive content-only term from the invoice's line items (proves full-text search
                    // finds it by content); fall back to the document name if the async index isn't ready yet.
                    var input = page.Locator("input[placeholder*='Search by name']");
                    var results = page.Locator(".wb-search-results .wb-list-row");
                    if (!await TrySearchAsync(page, input, results, "Wolframcarbid", attempts: 10))
                    {
                        await TrySearchAsync(page, input, results, "Invoice", attempts: 3);
                    }

                    await page.WaitForTimeoutAsync(800);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[web] enrich '{name}' skipped: {ex.Message}");
        }
    }

    // Types a query and submits, retrying until a result row appears (content indexing is async). Returns whether
    // a result showed up within the attempt budget.
    private static async Task<bool> TrySearchAsync(IPage page, ILocator input, ILocator results, string term, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            await input.FillAsync(term);
            await input.PressAsync("Enter");
            try
            {
                await results.First.WaitForAsync(new() { Timeout = 2000 });
                return true;
            }
            catch
            {
                await page.WaitForTimeoutAsync(500);
            }
        }

        return false;
    }

    private static async Task LoginAsync(IPage page)
    {
        await page.GetByText(LoginRegex()).First.ClickAsync();
        await page.WaitForSelectorAsync("input[name='Email'], input[type='email']");
        await page.FillAsync("input[name='Email'], input[type='email']", SelfHostedApp.AdminEmail);
        await page.FillAsync("input[name='Password'], input[type='password']", SelfHostedApp.AdminPassword);
        await page.ClickAsync("button[type='submit'], input[type='submit']");
        // Back in the SPA, authenticated — the display name shows in the app bar.
        await page.Locator(".wb-appbar").GetByText(SelfHostedApp.AdminDisplayName).WaitForAsync();
    }

    private static async Task ShotAsync(IPage page, string outDir, string name)
    {
        // Defensive: reset any horizontal scroll so every shot frames the workbench from the left.
        await page.EvaluateAsync("() => { window.scrollTo(0, 0); document.querySelectorAll('.wb, [data-pane]').forEach(e => e.scrollLeft = 0); }");
        var path = Path.Combine(outDir, $"web-{name}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
        Console.WriteLine($"[web] {name} → {Path.GetFileName(path)}");
    }

    [GeneratedRegex("^log ?in$", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRegex();
}
