using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.DesktopClient.Views;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The standalone-window screenshot hooks (issue #466): each renders ONE dialog or window headlessly to PNG —
/// surfaces no full-app screenshot can show, checked without a display.
/// </summary>
/// <remarks>
/// Moved verbatim out of <c>Program.cs</c>, which is on the 1000-line standing-debt list and is a switchboard:
/// it dispatches, and these bodies were a quarter of it. Same recipe as <see cref="SearchFieldCheck"/> and
/// <see cref="OpenShortcutCheck"/>, batched because the nine hooks are one kind of thing.
/// </remarks>
public static class WindowShots
{
    /// <summary>Runs the matching hook, if any; false means the argument belonged to somebody else.</summary>
    public static bool TryRun(string[] args)
    {
        // Headless render of the annotation dialog in SHAPE (markup) mode — verifies the palette is offered with
        // an optional label so a highlight can be recoloured (ADR "Annotation shape recolour"):
        // `--annotationshape-screenshot <out.png>`.
        var annShapeShotIndex = Array.IndexOf(args, "--annotationshape-screenshot");
        if (annShapeShotIndex >= 0 && annShapeShotIndex + 1 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var shapeDialog = new AnnotationDialog("", "#8BC34A", "Demo Admin", canEdit: true, canDelete: true, isShape: true);
            shapeDialog.Show();
            Dispatcher.UIThread.RunJobs();
            shapeDialog.CaptureRenderedFrame()?.Save(args[annShapeShotIndex + 1]);
            return true;
        }

        // Headless render of the sort & rotate dialog over a REAL multi-page PDF (#522, manual figure #527):
        // `--sortdialog-screenshot <out.png> <pdf>`. The sample's mis-rotated page 4 is turned a quarter
        // right first, so the figure shows the feature rather than a grid of upright tiles — and rendering
        // through PreviewRenderer means the same PDFium path the product uses is what produced the pictures.
        var sortShotIndex = Array.IndexOf(args, "--sortdialog-screenshot");
        if (sortShotIndex >= 0 && sortShotIndex + 2 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var pages = Services.PreviewRenderer.RenderPdfPages(File.ReadAllBytes(args[sortShotIndex + 2]));
            var sortDialog = new SortPagesDialog(Path.GetFileName(args[sortShotIndex + 2]), pages.Cast<Avalonia.Media.Imaging.Bitmap?>().ToList());
            // The sample batch's page 4 is the deliberately mis-rotated one — show it mid-fix, a quarter
            // turn from upright, so the figure carries both the problem and the remedy.
            if (sortDialog.Pages.Count > 3)
            {
                sortDialog.Pages[3].RotateRight();
            }

            sortDialog.Show();
            Dispatcher.UIThread.RunJobs();
            sortDialog.CaptureRenderedFrame()?.Save(args[sortShotIndex + 1]);
            return true;
        }

        // Headless render of the move/reference drop dialog — catches XAML/icon load crashes:
        // `--dropdialog-screenshot <out.png>`.
        var dropShotIndex = Array.IndexOf(args, "--dropdialog-screenshot");
        if (dropShotIndex >= 0 && dropShotIndex + 1 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var dropDialog = new DropActionDialog("Move 'Quarterly Report.pdf' here, or place a reference (shortcut) that leaves it where it is?");
            dropDialog.Show();
            Dispatcher.UIThread.RunJobs();
            dropDialog.CaptureRenderedFrame()?.Save(args[dropShotIndex + 1]);
            return true;
        }

        // Headless render of the server manager (ADR "Desktop server configuration") — catches XAML/binding load
        // crashes: `--servers-screenshot <out.png>`.
        var serversShotIndex = Array.IndexOf(args, "--servers-screenshot");
        if (serversShotIndex >= 0 && serversShotIndex + 1 < args.Length)
        {
            // As for `--logon-screenshot`: an optional `--lang <code>` renders this window in that language. It is
            // shown before the main window builds, so its strings are only ever seen in the language chosen at the
            // logon window — which makes a headless per-language render the only cheap way to check them (#417).
            var serversLangIndex = Array.IndexOf(args, "--lang");
            if (serversLangIndex >= 0 && serversLangIndex + 1 < args.Length)
            {
                SimplArchive.Localization.Culture.Apply(args[serversLangIndex + 1]);
            }

            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var win = new Views.ServerManagerWindow();
            var green = args.Contains("--green");
            var editShot = args.Contains("--edit");
            ViewModels.ServerManagerViewModel? gvm = null;
            // `--green` shows the light-green "confirmed our server" tint (issue #270): on the read-only pane of a
            // merely-selected profile, or — with `--edit` — on the focused edit field. The real tint needs a
            // reachable server to probe.
            if (green)
            {
                gvm = new ViewModels.ServerManagerViewModel();
                gvm.Servers.Add(new Services.ServerProfile { Name = "Demo", ApiRootUrl = "https://demo.simplarchive.dev" });
                gvm.Selected = gvm.Servers[^1];
                win.DataContext = gvm;
                if (editShot)
                {
                    gvm.EditCommand.Execute(null); // show the editable pane
                }
            }

            win.Show();
            Dispatcher.UIThread.RunJobs();
            // Set the tint after the ListBox selection binding has settled (attaching the DataContext momentarily
            // resets Selected, which clears the flag), then pump another layout pass so it renders.
            if (gvm is not null)
            {
                if (editShot)
                {
                    gvm.EditUrlIsOurServer = true;
                    Dispatcher.UIThread.RunJobs();
                    // `--focus` also exercises the focused visual state (the "while editing" case).
                    if (args.Contains("--focus"))
                    {
                        win.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>()
                            .FirstOrDefault(t => t.Name == "EditUrlBox")?.Focus();
                        Dispatcher.UIThread.RunJobs();
                    }
                }
                else
                {
                    gvm.SelectedIsOurServer = true;
                }

                Dispatcher.UIThread.RunJobs();
            }

            win.CaptureRenderedFrame()?.Save(args[serversShotIndex + 1]);
            return true;
        }

        // Headless render of the startup logon window (ADR "Desktop logon window") — catches XAML/binding load
        // Render the "Edit profile" dialog headlessly (#464): `--profile-screenshot <out.png>`.
        //
        // It is a Window, so no full-app screenshot can show it, and it is the one surface where the photo
        // crop is hosted INLINE rather than as its own dialog — a layout worth a check that does not need a
        // display. Rendered without an api client: the email and current photo stay empty, which is exactly
        // the not-yet-loaded state and still proves the window builds and lays out.
        var profileShotIndex = Array.IndexOf(args, "--profile-screenshot");
        if (profileShotIndex >= 0 && profileShotIndex + 1 < args.Length)
        {
            var langIndex = Array.IndexOf(args, "--lang");
            if (langIndex >= 0 && langIndex + 1 < args.Length)
            {
                SimplArchive.Localization.Culture.Apply(args[langIndex + 1]);
            }

            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();

            var profileWin = new Views.EditProfileDialog();
            profileWin.Show();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            profileWin.CaptureRenderedFrame()?.Save(args[profileShotIndex + 1]);
            return true;
        }

        // crashes: `--logon-screenshot <out.png>`.
        var logonShotIndex = Array.IndexOf(args, "--logon-screenshot");
        if (logonShotIndex >= 0 && logonShotIndex + 1 < args.Length)
        {
            // An optional `--lang <code>` renders the localized UI in that language (ADR "Desktop UI localization").
            var langIndex = Array.IndexOf(args, "--lang");
            if (langIndex >= 0 && langIndex + 1 < args.Length)
            {
                SimplArchive.Localization.Culture.Apply(args[langIndex + 1]);
            }

            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var logonVm = new ViewModels.LogonViewModel();
            // `--update` injects a sample self-update result (issue #271) so the shot shows that layout, since the
            // real check needs a reachable download area (the window's Activate() runs this seam on open).
            if (args.Contains("--update"))
            {
                logonVm.UpdateCheck = (_, _) => System.Threading.Tasks.Task.FromResult<Services.UpdateInfo?>(
                    new Services.UpdateInfo("2.0.0", "https://demo.simplarchive.dev/download/clients/macos/SimplArchive-2.0.0-x64.dmg", Services.ClientUpdateKind.UpdateAvailable));
            }

            var win = new Views.LogonWindow { DataContext = logonVm };
            win.Show();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            win.CaptureRenderedFrame()?.Save(args[logonShotIndex + 1]);
            return true;
        }

        // Headless render of the two structured editors (#564, ADR 0631) — catches XAML/binding load crashes,
        // and is the only way to LOOK at a dialog on a machine with no display:
        // `--contact-screenshot <out.png>` / `--appointment-screenshot <out.png>`.
        //
        // Populated with a card and an entry shaped like a real one — several e-mails, an address, attendees,
        // reminders, a recurrence — because an empty form renders fine while the one a user actually opens can
        // clip, overlap or scroll. Optional `--lang <code>` renders it localized.
        var contactShotIndex = Array.IndexOf(args, "--contact-screenshot");
        var apptShotIndex = Array.IndexOf(args, "--appointment-screenshot");
        if ((contactShotIndex >= 0 && contactShotIndex + 1 < args.Length)
            || (apptShotIndex >= 0 && apptShotIndex + 1 < args.Length))
        {
            var langAt = Array.IndexOf(args, "--lang");
            if (langAt >= 0 && langAt + 1 < args.Length)
            {
                SimplArchive.Localization.Culture.Apply(args[langAt + 1]);
            }

            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();

            // `--create` renders the SAME dialog in New mode (#631): empty, and — with two candidate
            // collections — showing the "file it into…" picker, which no edit render ever displays. It is the
            // only way to look at the row that decides where a new item lands.
            var createShot = args.Contains("--create");

            Avalonia.Controls.Window dialog;
            string outPath;
            if (contactShotIndex >= 0 && createShot)
            {
                var blank = new ViewModels.ContactEditViewModel();
                // "headless" rather than a plausible address: this shot never follows one, and a composed
                // api/ URL here would be indistinguishable from a real violation to the guard that forbids them.
                blank.OpenForCreate(
                [
                    new ViewModels.CreateTarget("Personal / My Addressbook", "headless"),
                    new ViewModels.CreateTarget("Sales / Customers", "headless"),
                ]);
                dialog = new Views.ContactDialog(blank);
                outPath = args[contactShotIndex + 1];
            }
            else if (apptShotIndex >= 0 && createShot)
            {
                var blank = new ViewModels.AppointmentEditViewModel();
                blank.OpenForCreate(
                [
                    new ViewModels.CreateTarget("Personal / My Calendar", "headless"),
                    new ViewModels.CreateTarget("Team / Releases", "headless"),
                ]);
                dialog = new Views.AppointmentDialog(blank);
                outPath = args[apptShotIndex + 1];
            }
            else if (contactShotIndex >= 0)
            {
                var card = new ViewModels.ContactEditViewModel
                {
                    GivenName = "Anna",
                    FamilyName = "Meyer",
                    Organization = "Contoso",
                    Title = "Head of Procurement",
                    Birthday = "1990-02-15",
                    Url = "https://contoso.example",
                    Note = "Met at the trade fair.",
                    StoredFormattedName = "Anna Meyer",
                };
                card.Emails.Add(new ViewModels.ContactFieldRowViewModel { Value = "anna@example.test", Type = "work" });
                card.Emails.Add(new ViewModels.ContactFieldRowViewModel { Value = "anna.private@example.test", Type = "home" });
                card.Phones.Add(new ViewModels.ContactFieldRowViewModel { Value = "+41 79 000 00 00", Type = "mobile" });
                card.Addresses.Add(new ViewModels.ContactAddressRowViewModel
                {
                    Type = "work",
                    Street = "Bahnhofstrasse 1",
                    City = "Zurich",
                    PostalCode = "8001",
                    Country = "Switzerland",
                });
                dialog = new Views.ContactDialog(card);
                outPath = args[contactShotIndex + 1];
            }
            else
            {
                var appointment = new ViewModels.AppointmentEditViewModel
                {
                    Summary = "Weekly sync",
                    Location = "Room 3",
                    Description = "Agenda in the shared folder.",
                    StartDate = new DateTimeOffset(new DateTime(2026, 9, 1), TimeSpan.Zero),
                    StartTime = new TimeSpan(14, 0, 0),
                    EndDate = new DateTimeOffset(new DateTime(2026, 9, 1), TimeSpan.Zero),
                    EndTime = new TimeSpan(15, 0, 0),
                    TimeZoneId = "Europe/Zurich",
                    RecurrenceRule = "FREQ=WEEKLY",
                    ReminderCount = 2,
                };
                appointment.Attendees.Add(new ViewModels.AttendeeRowViewModel("Tom Fischer", "tom@example.test", "ACCEPTED"));
                appointment.Attendees.Add(new ViewModels.AttendeeRowViewModel("Eva Rossi", "eva@example.test", "NEEDS-ACTION"));
                dialog = new Views.AppointmentDialog(appointment);
                outPath = args[apptShotIndex + 1];
            }

            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            dialog.CaptureRenderedFrame()?.Save(outPath);
            return true;
        }

        // Headless render of the connection-lost dialog (admin variant, details expanded) — catches XAML load
        // crashes: `--connlost-screenshot <out.png>`.
        var connLostShotIndex = Array.IndexOf(args, "--connlost-screenshot");
        if (connLostShotIndex >= 0 && connLostShotIndex + 1 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var dialog = new Views.ConnectionLostDialog(showDetails: true,
                "System.Net.Http.HttpRequestException: Connection refused (localhost:8080)\n   at SimplArchive.DesktopClient.Services.SimplArchiveApiClient...");
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            dialog.CaptureRenderedFrame()?.Save(args[connLostShotIndex + 1]);
            return true;
        }


        // Headless render of the OCR-language picker dialog (with sample selection) — catches XAML/binding load
        // crashes: `--ocrpicker-screenshot <out.png>`.
        var ocrPickerShotIndex = Array.IndexOf(args, "--ocrpicker-screenshot");
        if (ocrPickerShotIndex >= 0 && ocrPickerShotIndex + 1 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var catalog = new List<Services.SimplArchiveApiClient.OcrLanguageOption>
            {
                new("eng", "English"), new("enm", "English, Middle (1100–1500)"), new("deu", "German"),
                new("fra", "French"), new("ita", "Italian"), new("spa", "Spanish"),
                new("spa_old", "Spanish, Castilian – Old"), new("por", "Portuguese"), new("ron", "Romanian"), new("rus", "Russian"),
            };
            var picker = new OcrLanguagePickerViewModel(catalog, ["deu", "fra"]);
            var dialog = new OcrLanguagePickerDialog { DataContext = picker };
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            dialog.CaptureRenderedFrame()?.Save(args[ocrPickerShotIndex + 1]);
            return true;
        }

        // Headless render of the references dialog — catches XAML/binding load crashes:
        // `--referencesdialog-screenshot <out.png>`.
        var refShotIndex = Array.IndexOf(args, "--referencesdialog-screenshot");
        if (refShotIndex >= 0 && refShotIndex + 1 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var refsVm = new ViewModels.ReferencesViewModel(new Services.SimplArchiveApiClient("headless"), Guid.Empty, "Quarterly Report.pdf", "api");
            refsVm.PrimaryLocation = new ViewModels.ReferencingFolderViewModel { Id = Guid.NewGuid(), Name = "Invoices", Path = "Repositories / Contracts / Invoices" };
            refsVm.Items.Add(new ViewModels.ReferencingFolderViewModel { Id = Guid.NewGuid(), Name = "2026", Path = "Repositories / Contracts / 2026" });
            refsVm.Items.Add(new ViewModels.ReferencingFolderViewModel { Id = Guid.NewGuid(), Name = "Shared", Path = "Repositories / Team / Shared" });
            refsVm.Status = "Referenced in 2 folder(s).";
            var refsDialog = new ReferencesDialog { DataContext = refsVm };
            refsDialog.Show();
            Dispatcher.UIThread.RunJobs();
            refsDialog.CaptureRenderedFrame()?.Save(args[refShotIndex + 1]);
            return true;
        }

        // Renders the context-aware filing dialog (a document is selected on the Repositories tab, so it offers
        // file-as-version / file-in-folder / pick, ADR "Context-aware inbox filing dialog"): `--filedialog-screenshot <out.png>`.
        var fileShotIndex = Array.IndexOf(args, "--filedialog-screenshot");
        if (fileShotIndex >= 0 && fileShotIndex + 1 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            var bulk = args.Contains("--bulk");
            var ctx = bulk
                ? new ViewModels.DocumentFilingContext(Guid.Empty, "", "", Guid.NewGuid(), "Invoices", "Repositories / Demo Repository / Invoices")
                : new ViewModels.DocumentFilingContext(
                    Guid.NewGuid(), "Quarterly Report.pdf", "Repositories / Demo Repository / Invoices / Quarterly Report.pdf",
                    Guid.NewGuid(), "Invoices", "Repositories / Demo Repository / Invoices");
            var pickerVm = new ViewModels.FolderPickerViewModel(new Services.SimplArchiveApiClient("headless"), ctx, bulk);
            pickerVm.Roots.Add(new ViewModels.TreeNodeViewModel(Guid.NewGuid(), "Demo Repository", true, _ => Task.FromResult(Enumerable.Empty<ViewModels.TreeNodeViewModel>())));
            var fileDialog = new FolderPickerDialog { DataContext = pickerVm };
            fileDialog.Show();
            Dispatcher.UIThread.RunJobs();
            fileDialog.CaptureRenderedFrame()?.Save(args[fileShotIndex + 1]);
            return true;
        }
        return false;
    }
}
