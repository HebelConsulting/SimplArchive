using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Renders the MAIN WINDOW to a PNG for <c>--screenshot</c> and its variants — the tab selectors
/// (<c>--search</c>, <c>--inbox</c>, <c>--audit</c>, …) and the modifiers (<c>--demo</c>, <c>--dark</c>,
/// <c>--narrow</c>, <c>--menu</c>, <c>--marked</c>, …).
/// </summary>
/// <remarks>
/// <para>
/// A native GUI needs a display, so these captures stand in for looking at the app (CLAUDE.md, "Headless
/// verification hooks"). They are the only way to answer "does this LOOK right" without one, which is a
/// different question from "does it exist" — a control drawn with an unresolved brush is present, bound and
/// invisible.
/// </para>
/// <para>
/// The logon and server-manager windows are NOT here — they render in <see cref="WindowShots"/>, which owns
/// <c>--logon-screenshot</c> and <c>--servers-screenshot</c>. This summary claimed them when it was first
/// written, from the flag list rather than from the code; they had been a separate class for some time.
/// </para>
/// <para>
/// Extracted from <c>Program</c> because rendering a screen is not dispatching a command line. Program.cs was
/// over the 1000-line limit precisely because every hook's BODY lived in it, and the two fat ones — this and
/// <see cref="Services.ApiClientChecks"/> — were two thirds of the excess. Its ceiling note had said so since
/// the file re-entered the debt list.
/// </para>
/// </remarks>
internal static class ScreenshotRenderer
{
    internal static void Render(string path, bool demo, string? pdfPath = null)
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        if (Environment.GetCommandLineArgs().Contains("--dark") && Application.Current is { } app)
        {
            app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        }

        var viewModel = new MainWindowViewModel();
        // `--envbanner <id>` renders the environment strip (#501) — the only way to see it headlessly, since
        // it otherwise exists only after a real login chose a real profile.
        var envIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--envbanner");
        if (envIndex >= 0)
        {
            viewModel.EnvBanner.Set(Environment.GetCommandLineArgs()[envIndex + 1]);
        }

        if (demo)
        {
            if (Environment.GetCommandLineArgs().Contains("--search"))
            {
                viewModel.PopulateSearchDemoForScreenshot();
            }
            else if (Environment.GetCommandLineArgs().Contains("--intray"))
            {
                viewModel.PopulateIntrayDemoForScreenshot();
            }
            else if (Environment.GetCommandLineArgs().Contains("--hitoverlay"))
            {
                PopulateHitOverlay(viewModel);
            }
            else if (Environment.GetCommandLineArgs().Contains("--maskedit"))
            {
                viewModel.PopulateMaskEditForScreenshot();
            }
            else if (Environment.GetCommandLineArgs().Contains("--workflow"))
            {
                viewModel.PopulateWorkflowDemoForScreenshot();
                if (Environment.GetCommandLineArgs().Contains("--tasks"))
                {
                    viewModel.SelectedTab = 5; // Tasks tab
                }
            }
            else if (Environment.GetCommandLineArgs().Contains("--users"))
            {
                viewModel.PopulateUsersGroupsDemoForScreenshot();
                viewModel.SelectedTab = 6; // Users & groups tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--audit"))
            {
                viewModel.PopulateAuditDemoForScreenshot();
                viewModel.SelectedTab = 7; // Audit tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--recyclebin"))
            {
                viewModel.IsLoggedIn = true;
                viewModel.RecycleBin.PopulateDemoForScreenshot();
                viewModel.SelectedTab = 4; // Recycle bin tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--legalholds"))
            {
                viewModel.PopulateLegalHoldsDemoForScreenshot();
                viewModel.SelectedTab = 8; // Legal holds tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--retention"))
            {
                viewModel.PopulateRetentionDemoForScreenshot();
                viewModel.SelectedTab = 9; // Retention tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--tagstab"))
            {
                viewModel.PopulateTagsDemoForScreenshot();
                viewModel.SelectedTab = 12; // Tag catalog tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--tenant"))
            {
                viewModel.PopulateTenantSettingsDemoForScreenshot();
                viewModel.SelectedTab = 10; // Tenant tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--contacts"))
            {
                viewModel.IsLoggedIn = true;
                viewModel.ContactsTab.PopulateDemoForScreenshot();
                viewModel.SelectedTab = 13; // Contacts tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--calendar"))
            {
                viewModel.IsLoggedIn = true;
                viewModel.CalendarTab.PopulateDemoForScreenshot();
                viewModel.SelectedTab = 14; // Calendar tab
            }
            else if (Environment.GetCommandLineArgs().Contains("--checkout"))
            {
                viewModel.IsLoggedIn = true;
                viewModel.Checkout.PopulateDemoForScreenshot();
                viewModel.SelectedTab = 2; // Check-out tab
            }
            else
            {
                viewModel.PopulateDemoForScreenshot();
            }
        }

        if (pdfPath is not null)
        {
            viewModel.SetPreviewPagesForScreenshot(Services.PreviewRenderer.RenderPdfPages(File.ReadAllBytes(pdfPath)));
            if (demo)
            {
                // Match the seeded web annotations on the invoice (ADR 0502): a yellow sticky note over the first
                // line item + an amber highlight over the total row (Kind 0 = note, 1 = highlight; normalized coords).
                viewModel.SetPreviewNotesForScreenshot(
                [
                    new NoteBox(Guid.NewGuid(), 0, 0.085, 0.30, 0.30, 0.085, "#FFF59D", CanEdit: true, "Pos. 1: Preis gemäss Rahmenvertrag geprüft ✓"),
                    new NoteBox(Guid.NewGuid(), 1, 0.575, 0.49, 0.345, 0.03, "#FFD54A", CanEdit: true),
                ]);
            }
        }

        // Maximize the preview before first render so the full-screen overlay is arranged from the start.
        if (Environment.GetCommandLineArgs().Contains("--fullscreen"))
        {
            viewModel.Preview.PreviewFullscreen = true;
        }

        // `--narrow`: hand the preview's neighbours most of the width, so the preview column is about as narrow
        // as a user can drag it to. Issue #480's second half is only visible here — the default layout is wide
        // enough to hide a toolbar that does not wrap.
        if (Environment.GetCommandLineArgs().Contains("--narrow"))
        {
            viewModel.TreeWidth = new Avalonia.Controls.GridLength(4, Avalonia.Controls.GridUnitType.Star);
            viewModel.ListWidth = new Avalonia.Controls.GridLength(9, Avalonia.Controls.GridUnitType.Star);
            viewModel.ChatWidth = new Avalonia.Controls.GridLength(6, Avalonia.Controls.GridUnitType.Star);
        }

        var window = new MainWindow { DataContext = viewModel };

        // `--tall`: double the window height, for panes whose newest content lives below the default fold
        // (the Tenant tab's Modules section was the first) — a scrolling pane's bottom is otherwise
        // invisible to every capture, and an unlooked-at render is where defects live (the --menu lesson).
        if (Environment.GetCommandLineArgs().Contains("--tall"))
        {
            window.Height = 2100;
        }

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // `--fit-page`: the zoom that fits the whole page (#480), applied AFTER the first arrange because it is
        // measured from the pane the preview actually got.
        if (Environment.GetCommandLineArgs().Contains("--fit-page"))
        {
            viewModel.Preview.FitPageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        // `--marked`: put the selected-node ring on a real tree node so it can be looked at (#696). The ring is
        // the kind of thing that is perfectly true in the view-model and absent on screen, which no assertion
        // about IsMarked can catch.
        //
        // It marks a NAMED node rather than "the first child": the first attempt marked the demo tree's "…"
        // placeholder and rendered nothing, which read as a broken style and was not one. Two things had
        // changed between that render and the working one — the brush AND the target — and only re-running with
        // the original brush showed which mattered. It was the target.
        if (Environment.GetCommandLineArgs().Contains("--marked"))
        {
            var target = viewModel.Tree.FirstOrDefault(n => n.Name == "Demo Repository") ?? viewModel.Tree.First();
            target.IsMarked = true;
            Dispatcher.UIThread.RunJobs();
        }

        // `--menu`: open the tree context menu with its "New" submenu expanded, so the one part of this client
        // nobody could see gets into a screenshot.
        //
        // It turns out Avalonia's headless platform hosts a ContextMenu as an OVERLAY inside the window rather
        // than as a separate top-level, so CaptureRenderedFrame includes it — I had previously asserted the
        // opposite and left the desktop menu unlooked-at through three changes on the strength of that guess.
        // The first thing this flag rendered was a real defect: the submenu's entries were the only items in
        // that menu with no icon, because the ItemContainerTheme bound Header and Command and nothing else.
        //
        // The admits list is SYNTHETIC — demo data carries no masks, so the entries stand in for what a server
        // sends. That makes this a check on how the menu RENDERS (icons, alignment, nesting), not on which
        // entries a folder offers; the latter is CreatableChildrenTests and DesktopAdmitsMenuTests.
        if (Environment.GetCommandLineArgs().Contains("--menu"))
        {
            viewModel.TreeContextAdmits =
            [
                ViewModels.TreeMenuEntry.Create("Folder", "mdi-folder", () => { }),
                ViewModels.TreeMenuEntry.Create("Addressbook", "mdi-book-account", () => { }),
                ViewModels.TreeMenuEntry.Create("Calendar", "mdi-calendar", () => { }),
                // The two item kinds (#689). No real folder offers all five at once — an Addressbook offers
                // only Contact — but this list exists to check how an entry RENDERS, and an item's glyph
                // beside a folder's is the comparison worth being able to see.
                ViewModels.TreeMenuEntry.Create("Contact", "mdi-card-account-details", () => { }),
                ViewModels.TreeMenuEntry.Create("Appointment", "mdi-calendar-clock", () => { }),
            ];
            viewModel.TreeContextCanCreateAny = true;
            Dispatcher.UIThread.RunJobs();

            if (window.GetVisualDescendants().OfType<Avalonia.Controls.TreeView>().FirstOrDefault()?.ContextMenu is { } menu)
            {
                menu.Open();
                Dispatcher.UIThread.RunJobs();

                // The submenu has to be opened EXPLICITLY: it expands on hover, and a headless run has no
                // pointer, so without this the capture shows a collapsed "New ▸" and proves nothing.
                foreach (var item in menu.GetLogicalDescendants().OfType<Avalonia.Controls.MenuItem>().Where(m => m.ItemCount > 0))
                {
                    item.Open();
                }

                Dispatcher.UIThread.RunJobs();
            }
        }

        // Force a hovered word so the light-grey hover box (ADR "Copy a preview word to the clipboard") shows in
        // the otherwise-static screenshot.
        if (Environment.GetCommandLineArgs().Contains("--hitoverlay"))
        {
            var overlay = window.GetVisualDescendants().OfType<Views.HighlightOverlay>().FirstOrDefault();
            if (overlay?.Words is { } words && words.FirstOrDefault(w => w.Text == "Xylophonkatze") is { } word)
            {
                overlay.SetHoveredForScreenshot(word);
                Dispatcher.UIThread.RunJobs();
            }
        }

        // Collapse AFTER the first arrange (as an interactive click would), then re-arrange — this reproduces
        // the collapse re-layout path (which previously NRE'd when the GridSplitter was nested in a gutter Grid).
        if (Environment.GetCommandLineArgs().Contains("--collapsed"))
        {
            viewModel.ToggleTreeCommand.Execute(null);
            viewModel.ToggleChatCommand.Execute(null);
            viewModel.ToggleIndexCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        var frame = window.CaptureRenderedFrame();
        frame?.Save(path);
    }

    // Seeds the workbench with the sample scanned invoice + its real OCR word boxes and a find query, to show
    // the search hit-overlay (ADR "Search hit overlay") in a headless screenshot.
    private static void PopulateHitOverlay(MainWindowViewModel viewModel)
    {
        viewModel.PopulateDemoForScreenshot();

        var samplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "hitoverlay-sample.png");
        if (!File.Exists(samplePath))
        {
            return;
        }

        using var stream = File.OpenRead(samplePath);
        var image = new Avalonia.Media.Imaging.Bitmap(stream);

        // Real boxes measured from the sample invoice's OCR layout (normalized 0..1).
        var words = new List<Services.VersionsClient.TextLayoutBox>
        {
            new("Alpsteinwerk", 0.0871, 0.0490, 0.2315, 0.0251),
            new("RECHNUNG", 0.0831, 0.2668, 0.2395, 0.0257),
            new("Rechnungsnummer:", 0.0798, 0.3107, 0.2024, 0.0165),
            new("Rechnungsdatum:", 0.0790, 0.3347, 0.1831, 0.0165),
            new("Kennwort:", 0.0774, 0.3803, 0.1000, 0.0205),
            new("Xylophonkatze", 0.2379, 0.3854, 0.1492, 0.0160),
            new("Total:", 0.6581, 0.5981, 0.0589, 0.0205),
        };

        viewModel.PopulateHitOverlayForScreenshot(image, words, "Rechnung");
    }
}
