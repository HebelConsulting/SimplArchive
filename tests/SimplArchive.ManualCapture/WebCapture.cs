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

    /// <summary>Figures whose capture was skipped — reported at the end, and non-empty means a STALE figure.</summary>
    /// <remarks>
    /// Each capture swallows its own failure so one bad step does not cost the other thirty shots. The cost of
    /// that is invisible staleness: a skipped capture leaves the PREVIOUS PNG in place, so the manual keeps
    /// shipping a picture of an older app with nothing failing anywhere. That is how the personal-space figure
    /// went on being published after the space stopped being called "Personal" (ADR 0671) — the capture timed
    /// out looking for a node by its old name, printed one line among hundreds, and the stale figure shipped.
    ///
    /// So the swallow stays and the SILENCE goes: every skip is collected and named together at the end.
    /// </remarks>
    private static readonly List<string> Skipped = [];

    public static async Task RunAsync(string outDir)
    {
        Skipped.Clear();
        // Freeze the app's demo clock so the audit / tasks / my-work screens are byte-stable run-to-run (ADR 0510).
        // Matches the desktop capture's fixed clock (MainWindowViewModel.ScreenshotClock) so both halves of the
        // manual read the same date.
        // WithOcrSidecar: the external-link landing figure is supposed to show the document thumbnail (#476),
        // and nothing else in the deployment can rasterise a PDF. Only this harness asks for it — the UI suites
        // would pay the image build for a picture they never take.
        await using var app = new SelfHostedApp { DemoClock = "2026-06-01T09:00:00Z", WithOcrSidecar = true };
        Console.WriteLine("[web] booting the self-hosted app (Postgres + SeaweedFS + OpenSearch + Tika + Gotenberg + API)…");
        await app.StartAsync();
        Console.WriteLine($"[web] app ready at {app.BaseUrl}");

        using var playwright = await Playwright.CreateAsync();
        // Software rasterization (#832): GPU compositing rounds an overlay's alpha blend ±1/255 differently
        // per run — the tablet figure's standing touch affordance (a composited circle) was the last file
        // that would not byte-stabilize, its entire diff single-LSB. CPU raster is deterministic.
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "chrome",
            Headless = true,
            Args =
            [
                "--disable-gpu", "--disable-gpu-compositing",
                // Chromium's own pixel-test determinism set: without it, anti-aliased edges blend ±1/255
                // differently per run (measured: two figures whose entire diff was single-LSB).
                "--deterministic-mode", "--force-color-profile=srgb", "--disable-lcd-text",
                "--disable-partial-raster", "--disable-skia-runtime-opts", "--force-device-scale-factor=1",
            ],
        });
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

        // The notification bell's unread badge is fetched during MainLayout's init, which lands AFTER login — so
        // every figure with an app bar was a coin flip on whether the badge had arrived (#868). That is the churn
        // this harness kept producing: web-phone-detail.png alternated on nothing but that badge.
        //
        // Waiting for the response is the fix rather than hiding the badge, because the badge is real UI: the
        // manual should show what a user sees, and what it must not do is show it only half the time. Armed
        // BEFORE login (the response can land before the await otherwise) and bounded, so a build where the call
        // never happens degrades to the old timing rather than hanging for the 60 s page timeout.
        await LoginAndSettleBadgeAsync(page);

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
        await CaptureMobileTiersAsync(context, app.BaseUrl, outDir);

        // Named together, at the end, where they cannot be lost among the hundreds of lines above. A skipped
        // figure is a STALE figure — the previous PNG is still on disk and still ships.
        if (Skipped.Count > 0)
        {
            Console.WriteLine($"[web] {Skipped.Count} FIGURE(S) NOT REGENERATED — the manual will ship the "
                + $"PREVIOUS picture for each: {string.Join(", ", Skipped)}");
        }
    }

    /// <summary>The phone and tablet tiers (#684) — captured from the real app, in a TOUCH context.</summary>
    /// <remarks>
    /// <para>
    /// Its own browser context, because <c>HasTouch</c> can only be set when a context is created and the
    /// tablet tier keys on the pointer being <b>coarse</b> (ADR 0659). A capture that set only the viewport
    /// would quietly photograph the DESKTOP layout and file it under a tablet name — worse than no screenshot,
    /// because it would look like documentation.
    /// </para>
    /// <para>
    /// Logged in at desktop size and resized afterwards: the login wait looks for the display name in the app
    /// bar, and the responsive CSS hides it on a narrow screen — logging in at phone size hangs on an element
    /// that is present and hidden.
    /// </para>
    /// </remarks>
    private static async Task CaptureMobileTiersAsync(IBrowserContext desktop, string baseUrl, string outDir)
    {
        await using var touch = await desktop.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
            ColorScheme = ColorScheme.Light,
            HasTouch = true,
        });
        await touch.AddInitScriptAsync("try { localStorage.setItem('sa.desktopClientNoticeDismissed', '1'); } catch (e) { }");

        var page = await touch.NewPageAsync();
        page.SetDefaultTimeout(60000);
        await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByText(LoginRegex()).First.WaitForAsync();
        await LoginAndSettleBadgeAsync(page);

        // The premise, asserted rather than assumed: without a coarse pointer every shot below is the desktop
        // layout under a mobile filename.
        if (!await page.EvaluateAsync<bool>("() => matchMedia('(pointer: coarse)').matches"))
        {
            Console.WriteLine("[web] mobile tiers SKIPPED — touch emulation did not produce a coarse pointer");
            return;
        }

        var list = page.Locator("[data-pane='list']");

        // Between tiers, start from a clean page. The phone's detail overlay stays open across a resize and
        // covers everything — its find-in-document field then intercepts the click meant for the tree, which
        // reads as a mysterious timeout rather than "something is on top".
        async Task ResetAsync()
        {
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator(".wb-tabs").First.WaitForAsync();
            await page.WaitForTimeoutAsync(800);
        }

        // On the single-pane tiers the tree is a DRAWER translated off-screen, so the repository label exists
        // and cannot be clicked — the click retries until it times out. Open the drawer first where there is
        // one; choosing a folder closes it again. On a landscape tablet the tree is inline and there is none.
        async Task OpenDemoRepositoryAsync()
        {
            var hamburger = page.GetByLabel("Folders").First;
            if (await hamburger.IsVisibleAsync())
            {
                await hamburger.ClickAsync();
                await page.WaitForTimeoutAsync(600);
            }

            await page.GetByText("Demo Repository").First.ClickAsync();
            await list.Locator(".wb-list-row").First.WaitForAsync();
            await page.WaitForTimeoutAsync(500);
        }

        // ---- Phone (<= 767): drawer, list, and the full-screen detail with its sub-tabs.
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForTimeoutAsync(800);
        await OpenDemoRepositoryAsync();
        await ShotAsync(page, outDir, "phone-list");

        await page.GetByLabel("Folders").First.ClickAsync();
        await page.WaitForTimeoutAsync(700);
        await ShotAsync(page, outDir, "phone-drawer");

        // Closed by toggling the hamburger. The scrim is the interactive way out for a user, but it is a bare
        // positioned div that Playwright will not call visible, so it cannot be clicked from here — and leaving
        // the drawer open would put a dimmed overlay across every shot that follows.
        await page.GetByLabel("Folders").First.ClickAsync();
        await page.WaitForTimeoutAsync(600);

        // A single tap opens a document full-screen on a phone — the detail sub-tabs are the figure.
        await DrillToDocumentAsync(page, list);
        await ShotAsync(page, outDir, "phone-detail");

        // ---- Tablet upright (coarse + >= 768 + portrait): one pane, like a phone.
        await ResetAsync();
        await page.SetViewportSizeAsync(1024, 1366);
        await page.WaitForTimeoutAsync(900);
        await OpenDemoRepositoryAsync();
        await ShotAsync(page, outDir, "tablet-portrait");

        // ---- Tablet sideways: tree | list while browsing...
        await ResetAsync();
        await page.SetViewportSizeAsync(1366, 1024);
        await page.WaitForTimeoutAsync(900);
        await OpenDemoRepositoryAsync();
        await ShotAsync(page, outDir, "tablet-landscape");

        // ...and list | detail once a document is selected, with the tree a tap away.
        await DrillToDocumentAsync(page, list);
        await ShotAsync(page, outDir, "tablet-landscape-detail");
    }

    // Walks Contracts -> Acme Corp -> the two-revision offer, the same document the desktop figures use.
    private static async Task DrillToDocumentAsync(IPage page, ILocator list)
    {
        foreach (var folder in new[] { "Contracts", "Acme Corp" })
        {
            await list.Locator(".wb-list-row").Filter(new LocatorFilterOptions { HasText = folder }).First.DblClickAsync();
            await page.WaitForTimeoutAsync(900);
        }

        await list.Locator(".wb-list-row").Filter(new LocatorFilterOptions { HasText = "Offer 2026-014" }).First.ClickAsync();
        await page.WaitForTimeoutAsync(2000);
    }

    // The Personal space expanded, showing the Intray and Check-out launchers — the figure for the manual's
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
            // The personal space is named after its OWNER (ADR 0671), not the literal "Personal" it used to be.
            // Filtering on the old name matched nothing, so this capture timed out and was SKIPPED — and because
            // a skip only prints a line and leaves the previous PNG in place, the manual kept shipping a figure
            // of an older app with nothing failing. A screenshot that silently does not regenerate is the same
            // class of defect as a test that silently does not assert.
            var personal = page.Locator("[data-pane='tree'] .mud-treeview-item-content")
                .Filter(new() { HasText = SelfHostedApp.AdminDisplayName }).First;
            await personal.WaitForAsync(new() { Timeout = 10000 });

            // Expanding is the arrow, not the node — clicking the node SELECTS it (and a launcher click would
            // switch tabs, which is the opposite of what this figure shows).
            await personal.Locator(".mud-treeview-item-arrow").ClickAsync();
            await page.Locator("[data-drop-intray]").First.WaitForAsync(new() { Timeout = 10000 });
            await page.WaitForTimeoutAsync(500);

            await ShotAsync(page, outDir, "personal-launchers");
        }
        catch (Exception e)
        {
            // A missing figure is better than a failed regeneration: the script also refreshes 30 other shots.
            Skipped.Add("personal-launchers");
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
            await NormalizeRunSpecificTextAsync(page);

            await ShotAsync(page, outDir, "webdav");
            await DismissAnyDialogAsync(page);
        }
        catch (Exception e)
        {
            // A missing figure is better than a failed regeneration: the script also refreshes 30 other shots.
            Skipped.Add("webdav");
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
            Skipped.Add("versions");
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
            Skipped.Add("chat");
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

            // Wait for the DIFF, not for a duration. A fixed pause is a guess about how long the comparison
            // takes, and on the machine that regenerates the manual the guess was wrong: the published figure
            // for the versioning chapter was a dialog with a spinner in it, which is a picture of the feature
            // not having happened yet. The comparison is the only thing this figure exists to show, so its
            // absence must fail the step rather than be captured.
            await dialog.Locator("[data-testid='compare-diff']").WaitForAsync(new() { Timeout = 30000 });
            await ShotAsync(page, outDir, "version-compare");
        }
        catch (Exception ex)
        {
            Skipped.Add("version-compare");
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
            await NormalizeRunSpecificTextAsync(page);
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
                    // Not `.wb-sysfields` alone: the FOLDER's detail is already on screen and satisfies that
                    // instantly, so one run shot Acme Corp's pane and the next the invoice's (#832's last
                    // straggler). The wait must name the subject the figure is about.
                    await page.Locator(".wb-sysfields").GetByText("Invoice 2026-003").First.WaitForAsync(new() { Timeout = 15000 });
                    // The chat pane fills asynchronously after the detail — one run shot the seeded thread,
                    // the next "No messages yet" (#832). The invoice's seeded approval message is the anchor.
                    await page.Locator("[data-pane='chat']").GetByText("Approved for payment").First.WaitForAsync(new() { Timeout = 15000 });
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
            Skipped.Add($"enrich:{name}");
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

    // Freezes every CSS animation and transition (#832): a MudBlazor ripple or fade caught mid-flight is a
    // different alpha value per run, and four figures churned on nothing else. Elements jump straight to
    // their settled state, which is also the state a figure should show.
    private static async Task FreezeMotionAsync(IPage page) =>
        await page.AddStyleTagAsync(new()
        {
            Content =
            "*, *::before, *::after { animation: none !important; transition: none !important; }"
            // The icon-button hover/focus circle is a composited overlay whose alpha blend lands ±1/255
            // differently per run (the tablet figure churned by exactly that, 372 pixels of it). An idle
            // figure has no pointer on it, so the circle is not content — suppress it for shots.
            + " .mud-icon-button:hover, .mud-icon-button:focus, .mud-icon-button:focus-visible { background-color: transparent !important; }"
            + " .mud-ripple::after, .mud-ripple-icon::after { display: none !important; }"
        });

    // Rewrites the run-specific values a figure must not depend on (#832): the app's ephemeral port becomes
    // the documented compose port, and freshly-minted secrets (the WebDAV password, an external link's token)
    // become fixed representative ones. The flows still RUN for real — only the displayed secret is replaced,
    // which is also one secret fewer printed in a public PDF. Same licence as the desktop harness's synthetic
    // admits list: the figure documents how the dialog RENDERS, not which random value this boot produced.
    private static async Task NormalizeRunSpecificTextAsync(IPage page) =>
        await page.EvaluateAsync(@"() => {
            const fixText = s => s
                .replace(/\/\/localhost:\d+/g, '//localhost:8080')
                .replace(/\b[0-9a-f]{24,}\b/g, '2fd4e1c67a2d28fced849ee1bb76e739')
                .replace(/(external-links\/)[A-Za-z0-9_-]{8,}/g, '$1wG2tP8rXo4kQhN0yLzB5aA');
            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
            for (let n = walker.nextNode(); n; n = walker.nextNode()) {
                const v = fixText(n.nodeValue || '');
                if (v !== n.nodeValue) { n.nodeValue = v; }
            }
            for (const i of document.querySelectorAll('input')) {
                const v = fixText(i.value || '');
                if (v !== i.value) { i.value = v; }
            }
        }");

    private static async Task LoginAsync(IPage page)
    {
        await page.GetByText(LoginRegex()).First.ClickAsync();
        await page.WaitForSelectorAsync("input[name='Email'], input[type='email']");
        await page.FillAsync("input[name='Email'], input[type='email']", SelfHostedApp.AdminEmail);
        await page.FillAsync("input[name='Password'], input[type='password']", SelfHostedApp.AdminPassword);
        await page.ClickAsync("button[type='submit'], input[type='submit']");
        // Back in the SPA, authenticated — the display name shows in the app bar.
        await page.Locator(".wb-appbar").GetByText(SelfHostedApp.AdminDisplayName).WaitForAsync();

        // Every logged-in page — main, touch tiers — renders motion-free (#832).
        await FreezeMotionAsync(page);
    }

    /// <summary>Logs in, then waits for the unread-count fetch that decides whether the bell shows a badge.</summary>
    /// <remarks>
    /// #868: that fetch lands during MainLayout's init, AFTER login, so a screenshot taken before it produced a
    /// bell without a badge and one taken after produced a bell with one — the same figure, two bytes, no code
    /// change between them. Both responsive and desktop passes go through here so neither can drift alone.
    /// </remarks>
    private static async Task LoginAndSettleBadgeAsync(IPage page)
    {
        var unreadCount = page.WaitForResponseAsync(
            r => r.Url.Contains("/api/notifications/unread-count", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 15000 });

        await LoginAsync(page);

        try
        {
            await unreadCount;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("[web] the unread-count fetch never arrived — figures may show the pre-badge state");
        }

        // …and one render tick, so the badge has actually painted once the count is in.
        await page.WaitForTimeoutAsync(300);
    }

    private static async Task ShotAsync(IPage page, string outDir, string name)
    {
        // Drop focus and park the pointer before every shot (#832): where focus and the mouse happen to
        // rest after the setup clicks is a race — a focus ring, a blinking caret, or a hover tint that wins
        // it in one run and loses it in the next is a byte-difference with nothing behind it (the webdav
        // figure churned by exactly a button's hover edges after the dialog relayouted under the pointer).
        // The figures document content, not the pointer's history.
        await page.EvaluateAsync("() => { const a = document.activeElement; if (a && a !== document.body) { a.blur(); } }");
        await page.Mouse.MoveAsync(0, 0);
        // Defensive: reset any horizontal scroll so every shot frames the workbench from the left.
        await page.EvaluateAsync("() => { window.scrollTo(0, 0); document.querySelectorAll('.wb, [data-pane]').forEach(e => e.scrollLeft = 0); }");
        // A transient toast is the same class of race as focus and hover above, and it is worse: it does not
        // shift a few pixels, it COVERS the thing being documented. The v0.11.0 cut regenerated
        // web-tablet-portrait.png with a "Review overdue" workflow reminder sitting across the ribbon, hiding
        // Refresh / WebDAV / My external links — a defaced figure that would have shipped in the manual with
        // nothing failing. Which notifications happen to fire while the app is being photographed is not
        // something the figures document, so remove them before the shot rather than hoping the timing misses.
        await page.EvaluateAsync("() => { document.querySelectorAll('.mud-snackbar, .mud-snackbar-container').forEach(e => e.remove()); }");
        var path = Path.Combine(outDir, $"web-{name}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
        Console.WriteLine($"[web] {name} → {Path.GetFileName(path)}");
    }

    [GeneratedRegex("^log ?in$", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRegex();
}
