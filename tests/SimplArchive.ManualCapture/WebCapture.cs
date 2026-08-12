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
        // Freeze the app's demo clock so the audit / tasks / my-work screens are byte-stable run-to-run (ADR 0510).
        // Matches the desktop capture's fixed clock (MainWindowViewModel.ScreenshotClock) so both halves of the
        // manual read the same date.
        await using var app = new SelfHostedApp { DemoClock = "2026-06-01T09:00:00Z" };
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
        // The "SimplArchive" app-bar text renders before the landing content: the login prompt is inside an
        // <AuthorizeView> that only fills in once the Blazor auth state resolves. Wait for the "Log in" button so
        // the before-login shot captures the actual login page, not an empty body under the app bar.
        await page.GetByText(LoginRegex()).First.WaitForAsync();

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
                // Tabs are icon-only (#298) — the label is in aria-label, not visible text.
                await page.Locator($".wb-tab[aria-label='{tab}']").First.ClickAsync();
                // Let the tab's panel render + any first-load fetch settle before the shot.
                await page.WaitForTimeoutAsync(1500);
            }

            await EnrichAsync(page, screen.Name);
            await ShotAsync(page, outDir, screen.Name);
        }

        await CapturePersonalLaunchersAsync(page, outDir);
        await CaptureVersionsAsync(page, outDir);
        await CaptureChatAsync(page, outDir);
        await CaptureVersionCompareAsync(page, outDir);
        await CaptureWebDavAsync(page, outDir);
        await CaptureExternalLinksAsync(context, page, outDir);
    }

    // The Personal space expanded, showing the Inbox and Check-out launchers — the figure for the manual's
    // "How documents get in" section (#467).
    //
    // Worth a bespoke capture rather than reusing the repositories shot: those two nodes are what a reader has to
    // find in order to use half the ingestion routes, and they are only visible once Personal is EXPANDED. A
    // screenshot of the collapsed tree would illustrate the section by omitting its subject.
    private static async Task CapturePersonalLaunchersAsync(IPage page, string outDir)
    {
        try
        {
            await page.Locator(".wb-tab[aria-label='Repositories']").First.ClickAsync();
            var personal = page.Locator("[data-pane='tree'] .mud-treeview-item-content")
                .Filter(new() { HasText = "Personal" }).First;
            await personal.WaitForAsync(new() { Timeout = 10000 });

            // Expanding is the arrow, not the node — clicking the node SELECTS it (and a launcher click would
            // switch tabs, which is the opposite of what this figure shows).
            await personal.Locator(".mud-treeview-item-arrow").ClickAsync();
            await page.Locator("[data-drop-inbox]").First.WaitForAsync(new() { Timeout = 10000 });
            await page.WaitForTimeoutAsync(500);

            await ShotAsync(page, outDir, "personal-launchers");
        }
        catch (Exception e)
        {
            // A missing figure is better than a failed regeneration: the script also refreshes 30 other shots.
            Console.WriteLine($"[web] personal-launchers skipped: {e.Message}");
        }
    }

    // Opens the WebDAV dialog from the Repositories ribbon and generates a password, so the shot shows the state a
    // user actually mounts from: the URL, the username, the one-shot password, and the per-OS mount steps.
    //
    // This figure is load-bearing, not decorative. ADR 0560 accepts that those steps will go stale — they name OS
    // UI paths ("Go ▸ Connect to Server") that vendors rename, in four languages, with no test that can detect it —
    // and names *a screenshot in the manual* as the only realistic check. Without this capture that check does not
    // exist, and the ADR's accepted cost is one nobody is positioned to notice.
    //
    // Generating is safe here: the demo stack is a throwaway container, and the password is app-specific — it is
    // not the login password, and revoking it is a button in the same dialog.
    private static async Task CaptureWebDavAsync(IPage page, string outDir)
    {
        try
        {
            await page.Locator(".wb-tab[aria-label='Repositories']").First.ClickAsync();
            await page.Locator("[data-tour='action-webdav']").First.ClickAsync();
            var dialog = page.Locator(".mud-dialog").First;
            await dialog.WaitForAsync(new() { Timeout = 10000 });

            // Generate, so the mount steps and the one-shot password are on screen. Before this the dialog says
            // only "not set up", which is the least informative of its states and the one a reader needs least.
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Generate password" }).ClickAsync();

            // Wait for the reveal itself rather than a fixed pause — shooting early would produce a figure of the
            // pre-generation dialog with nothing in the filename to say so.
            await dialog.GetByText("won't be shown again").WaitForAsync(new() { Timeout = 15000 });
            await page.WaitForTimeoutAsync(600);

            await ShotAsync(page, outDir, "webdav");
            await DismissAnyDialogAsync(page);
        }
        catch (Exception e)
        {
            // A missing figure is better than a failed regeneration: the script also refreshes 30 other shots.
            Console.WriteLine($"[web] webdav skipped: {e.Message.Split('\n')[0]}");
        }
    }

    // Selects the demo offer — Demo Repository → Contracts → Acme Corp → "Offer 2026-014" (ADR 0502).
    //
    // Three captures need this exact document rather than any document: it is the only seeded one with TWO
    // versions, per-version comments, and a chat thread, which is what makes the versions, compare and chat
    // figures show the feature rather than an empty shell (#469).
    private static async Task SelectDemoOfferAsync(IPage page)
    {
        await page.Locator(".wb-tab[aria-label='Repositories']").First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        var contracts = page.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" });
        await contracts.First.WaitForAsync(new() { Timeout = 15000 });
        await contracts.First.DblClickAsync();
        var acme = page.Locator(".wb-list-row").Filter(new() { HasText = "Acme Corp" });
        await acme.First.WaitForAsync(new() { Timeout = 15000 });
        await acme.First.DblClickAsync();
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = "Offer 2026-014" });
        await row.First.WaitForAsync(new() { Timeout = 15000 });
        await row.First.ClickAsync();
    }

    // The "Versions" dialog on the demo offer: both revisions with their COMMENTS, which is the point of the
    // figure — a version list without comments is a list of timestamps, and the comment is what makes it a
    // history someone can read (#469).
    private static async Task CaptureVersionsAsync(IPage page, string outDir)
    {
        try
        {
            await SelectDemoOfferAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Versions", Exact = true }).First.ClickAsync();
            var dialog = page.Locator(".mud-dialog").First;
            await dialog.WaitForAsync(new() { Timeout = 10000 });

            // Wait for the rows themselves: shooting on dialog-open alone can catch it before the version list
            // has loaded, producing a figure of an empty dialog.
            await dialog.Locator("tbody tr").First.WaitForAsync(new() { Timeout = 15000 });
            await page.WaitForTimeoutAsync(600);
            await ShotAsync(page, outDir, "versions");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[web] versions skipped: {ex.Message.Split('\n')[0]}");
        }
        finally
        {
            try { await DismissAnyDialogAsync(page); }
            catch (Exception ex) { Console.WriteLine($"[web] warning — could not close the versions dialog: {ex.Message.Split('\n')[0]}"); }
        }
    }

    // The per-document chat thread. Shot as the PANE rather than the whole window: at 1680px the chat is a 340px
    // column against a full workbench, so a page-wide figure would be a picture of everything else with the
    // subject in the corner (#469).
    private static async Task CaptureChatAsync(IPage page, string outDir)
    {
        try
        {
            await SelectDemoOfferAsync(page);
            var chat = page.Locator("[data-pane='chat']").First;
            await chat.WaitForAsync(new() { Timeout = 15000 });

            // The seeded thread — wait for a real message, not just the pane, so an empty thread cannot pass for
            // a captured one.
            await chat.Locator(".wb-chat-thread").First.WaitForAsync(new() { Timeout = 15000 });
            await page.WaitForTimeoutAsync(800);
            await chat.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(outDir, "web-chat.png") });
            Console.WriteLine("[web] chat → web-chat.png");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[web] chat skipped: {ex.Message.Split('\n')[0]}");
        }
    }

    // Opens the "Compare versions" dialog on the two-revision demo document ("Offer 2026-014", ADR 0502) and shots
    // the inline diff — the feature figure for the manual's versioning chapter.
    private static async Task CaptureVersionCompareAsync(IPage page, string outDir)
    {
        try
        {
            await SelectDemoOfferAsync(page);
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
        finally
        {
            // Leave no modal behind. A dialog left open has a scrim that intercepts every subsequent click, so it
            // breaks whatever capture runs next — and because each step is individually guarded, the damage shows
            // up as the NEXT step mysteriously "skipped" rather than as a failure here. In the `finally` so it
            // still runs when the shot above threw half-way.
            //
            // Swallowed HERE specifically, even though DismissAnyDialogAsync throws by design: a throw out of a
            // `finally` would discard whatever exception was already on its way up from the try block, hiding the
            // real failure behind a cleanup one. The next step calls it again outside a finally, so a genuinely
            // stuck dialog still surfaces — just attributed to the step it actually breaks.
            try
            {
                await DismissAnyDialogAsync(page);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[web] warning — could not close the version-compare dialog: {ex.Message}");
            }
        }
    }

    // Closes any open MudBlazor dialog and waits for its scrim to go, because a scrim left behind intercepts
    // every subsequent click.
    //
    // Escape is tried first but is NOT enough: the version-compare dialog ignores it (it opts out of
    // close-on-escape and offers only its CLOSE button), so the scrim stayed and the next capture's very first
    // click timed out. Hence the fallback to the dialog's own close control, and hence this throws rather than
    // logging on failure — a capture that continues past an undismissed modal cannot do anything but fail
    // sixty seconds later, somewhere that looks unrelated.
    private static async Task DismissAnyDialogAsync(IPage page)
    {
        var scrim = page.Locator(".mud-overlay-scrim");
        if (await scrim.CountAsync() == 0)
        {
            return;
        }

        await page.Keyboard.PressAsync("Escape");
        if (await WaitForScrimGoneAsync(scrim, 2000))
        {
            return;
        }

        // Any button whose label reads as a dismissal — Close on most dialogs, Cancel on the editing ones.
        var close = page.Locator(".mud-dialog button").Filter(new() { HasTextRegex = CloseButtonRegex() });
        if (await close.CountAsync() > 0)
        {
            await close.Last.ClickAsync();
            if (await WaitForScrimGoneAsync(scrim, 5000))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "a dialog is still open and its scrim will block every later click — neither Escape nor a Close/Cancel button dismissed it");
    }

    private static async Task<bool> WaitForScrimGoneAsync(ILocator scrim, float timeout)
    {
        try
        {
            await scrim.First.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = timeout });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException)
        {
            return true; // the element went away between the count and the wait — which is the outcome we wanted
        }
    }

    [GeneratedRegex("^(close|cancel)$", RegexOptions.IgnoreCase)]
    private static partial Regex CloseButtonRegex();

    // The external-links figures for the manual's "Sharing outside SimplArchive" chapter (issue #406, ADR 0546):
    // the per-document dialog at the moment the URL is shown, the cross-document list, and the anonymous landing
    // page a recipient sees.
    //
    // The link is CREATED here rather than reusing the seeded demo one, for two reasons. The create dialog reveals
    // the URL exactly once, at creation — that is the feature's central safety property and the thing the figure
    // has to show, and no listing can be made to display it. And the seeded token is derived from a fixed string
    // in DemoDataSeeder; re-deriving it here would duplicate the crypto in a project that deliberately does not
    // link the API assembly (ReferenceOutputAssembly=false), so the two copies could silently drift apart.
    private static async Task CaptureExternalLinksAsync(IBrowserContext context, IPage page, string outDir)
    {
        // Named steps, so a failure says WHERE. Without this the guard below reports only "Timeout 60000ms
        // exceeded" plus a scrolling log, which cannot distinguish "the button is missing" from "the document was
        // never selected" — three ten-minute capture runs went into that ambiguity.
        var step = "start";
        try
        {
            // Belt and braces: the previous step now tidies up after itself, but this must not silently break
            // again if some future capture is inserted before it and forgets.
            await DismissAnyDialogAsync(page);

            step = "open the Repositories tab";
            await page.Locator(".wb-tab[aria-label='Repositories']").First.ClickAsync();
            step = "select the Demo Repository root";
            await page.GetByText("Demo Repository").First.ClickAsync();
            // Contracts → MyCountry Telekom → the service agreement (the same document the demo seed shares).
            foreach (var folder in new[] { "Contracts", "MyCountry Telekom" })
            {
                step = $"drill into '{folder}'";
                var row = page.Locator(".wb-list-row").Filter(new() { HasText = folder });
                await row.First.WaitForAsync(new() { Timeout = 15000 });
                await row.First.DblClickAsync();
            }

            step = "select the service-agreement document";
            var doc = page.Locator(".wb-list-row").Filter(new() { HasText = "service agreement" });
            await doc.First.WaitForAsync(new() { Timeout = 15000 });
            await doc.First.ClickAsync();

            // The link icon sits in the detail header, and only when the API advertises the rel — so its absence
            // here would mean the right or the tenant switch is off, not that the selector is wrong.
            step = "open the External links dialog";
            await page.GetByRole(AriaRole.Button, new() { Name = "External links…" }).First.ClickAsync();
            var dialog = page.Locator(".mud-dialog").First;
            await dialog.WaitForAsync(new() { Timeout = 10000 });

            step = "click Create external link";
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Create external link…" }).ClickAsync();

            // Wait for the one-shot reveal itself rather than a fixed pause: shooting before it renders would
            // produce a figure of the empty form, with nothing in the filename to say so.
            //
            // NOT `input[readonly].First` — the date picker renders a read-only input too, so that would have
            // quietly screenshotted the right thing while reading the wrong value into `url`, and the landing-page
            // shot would then have navigated to a date.
            step = "wait for the one-shot URL reveal";
            await dialog.GetByText("shown only once").WaitForAsync(new() { Timeout = 15000 });
            var url = await dialog.Locator("input").EvaluateAllAsync<string>(
                "els => { const f = els.find(e => (e.value || '').startsWith('http')); return f ? f.value : ''; }");
            await page.WaitForTimeoutAsync(600);
            await ShotAsync(page, outDir, "external-link-create");

            await DismissAnyDialogAsync(page);

            // The cross-document list: every live link this user has shared, with Go to / Show / Revoke per row.
            step = "open My external links";
            await page.GetByRole(AriaRole.Button, new() { Name = "My external links" }).First.ClickAsync();
            await page.Locator(".mud-dialog tbody tr").First.WaitForAsync(new() { Timeout = 15000 });
            await page.WaitForTimeoutAsync(600);
            await ShotAsync(page, outDir, "external-links-list");
            await DismissAnyDialogAsync(page);

            // The recipient's view, in a SEPARATE context with no session — the whole point of the page is that it
            // works for someone with no account, and shooting it from the logged-in page would not prove that.
            if (!string.IsNullOrWhiteSpace(url))
            {
                var anon = await context.Browser!.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
                    ColorScheme = ColorScheme.Light,
                });
                var anonPage = await anon.NewPageAsync();
                await anonPage.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await anonPage.WaitForTimeoutAsync(1200);
                await ShotAsync(anonPage, outDir, "external-link-landing");
                await anon.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            // First line only: Playwright appends a long call log that buries the step name.
            Console.WriteLine($"[web] external-links FAILED at step '{step}': {ex.Message.Split('\n')[0]}");
            try
            {
                var dump = Path.Combine(Path.GetTempPath(), "manual-capture-external-links-failure.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = dump });
                Console.WriteLine($"[web] state at failure → {dump}");
            }
            catch { /* diagnostics are best-effort */ }
        }
    }

    // Screen-specific interactions that populate a tab with real content before the shot, so the web figures aren't
    // empty "nothing selected" states. Driven off the demo seed (ADR 0214): Demo Repository → Contracts → Acme Corp
    // → the "Invoice 2026-003" document. Best-effort — a failure here shouldn't abort the whole capture, so each block is
    // guarded (the tab still gets shot in its default state).
    private static async Task EnrichAsync(IPage page, string name)
    {
        try
        {
            switch (name)
            {
                case "repositories":
                    // Drill Demo Repository → Contracts → Acme Corp → select the document, so the detail + preview panes
                    // fill (the seeded invoice PDF renders via pdf.js, with the seeded highlight + sticky note on it).
                    await page.GetByText("Demo Repository").First.ClickAsync();
                    var contracts = page.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" });
                    await contracts.First.WaitForAsync(new() { Timeout = 15000 });
                    await contracts.First.DblClickAsync();
                    var acme = page.Locator(".wb-list-row").Filter(new() { HasText = "Acme Corp" });
                    await acme.First.WaitForAsync(new() { Timeout = 15000 });
                    await acme.First.DblClickAsync();
                    var doc = page.Locator(".wb-list-row").Filter(new() { HasText = "Invoice 2026-003" });
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
