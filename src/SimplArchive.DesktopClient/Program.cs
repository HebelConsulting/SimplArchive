using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.MaterialDesign;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.DesktopClient.Views;

namespace SimplArchive.DesktopClient;

internal static class Program
{
    // ATTACH_PARENT_PROCESS — attach to the console of whatever launched us, if there is one.
    private const uint AttachParentProcess = 0xFFFFFFFF;

    // DllImport rather than the newer LibraryImport: the latter's source generator emits unsafe code, which
    // would mean turning on AllowUnsafeBlocks for the whole project to gain nothing — this is one call with a
    // blittable argument and a bool return, which the classic marshaller handles without any of that.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [STAThread]
    public static void Main(string[] args)
    {
        // The app is a WINDOWS subsystem binary (#421), so Windows gives it no console — which is the point: a
        // console window that owns the GUI process kills the session when a user tidies it away. The cost is
        // that the headless verification hooks (--screenshot, --selftest, --render-pdf, the VM-check flags)
        // would print into nowhere when run from a terminal. So when there ARE arguments, we attach to the
        // launching console first.
        //
        // Any argument means a diagnostic run: a normal launch — Explorer, the taskbar, a shortcut — passes
        // none. That is a more durable test than listing the flags, which would silently rot as flags are added.
        // It must happen before ANY console use, because .NET binds the console streams lazily on first touch.
        // Nothing to do on macOS/Linux, where the process already has whatever stdout it was given.
        if (args.Length > 0 && OperatingSystem.IsWindows())
        {
            AttachConsole(AttachParentProcess); // false simply means there was no parent console — nothing to do
        }

        // The log comes up before anything that might want to write to it (ADR 0613). --verbose lifts the
        // console sink to Debug (the file always carries Debug); on Windows the attach above has already given
        // the flag a console to print into.
        Services.DesktopLog.Initialize(verbose: args.Contains("--verbose"));

        // A simplarchive:// launch (#761): the OS hands the link as an argument. Parked on the view-model and
        // consumed once the user is signed in and the workbench is loaded — a deep link cannot skip login.
        if (args.FirstOrDefault(a => a.StartsWith($"{Services.DeepLinks.Scheme}://", StringComparison.OrdinalIgnoreCase)) is { } schemeLink)
        {
            Services.DesktopLog.Debug("Deep link: launched with {Link}", schemeLink);
            ViewModels.MainWindowViewModel.PendingDeepLink = schemeLink;
        }

        // Registering the scheme is idempotent per-user work on Windows (HKCU needs no elevation); macOS reads
        // it from the app bundle's Info.plist and Linux from the packaged .desktop file, both set at packaging.
        Services.WindowsSchemeRegistration.EnsureRegistered();

        // Crash guard (ADR "Desktop crash guard"): surface unhandled background/unobserved exceptions in the
        // "lost connection" modal instead of taking the app down. UI-thread async-void handlers are guarded
        // separately via Safe.Fire.
        //
        // The dialog tells the user; the log keeps the DETAIL. Before this, the exception died with the dialog —
        // so "it crashed and I clicked OK" was the entire available evidence.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Services.DesktopLog.Fatal(ex, "Unhandled exception on {Thread}", Environment.CurrentManagedThreadId);
                Services.DesktopLog.Shutdown(); // the process may be going down; flush what we have
                Services.AppExceptions.Report(ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.DesktopLog.Error(e.Exception, "Unobserved task exception");
            Services.AppExceptions.Report(e.Exception);
            e.SetObserved();
        };

        // Register the Material Design Icons provider (backs the <i:Icon Value="mdi-…" /> glyphs).
        IconProvider.Current.Register<MaterialDesignIconProvider>();

        // Check that the app icon reaches EVERY window, not just MainWindow: `--icon-test` (#421).
        //
        // Worth a hook of its own because the failure is invisible: the icon lives on the TITLE BAR, which a
        // headless screenshot does not render, so a style that silently stopped applying would look identical
        // in every capture we take. Removing the style makes this report FAILED, which is what says the check
        // measures the style rather than some default.
        if (args.Contains("--icon-test"))
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();

            // A window that never set Icon= itself, so a pass can only come from the application-wide style.
            var window = new Views.LogonWindow();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var ok = window.Icon is not null;
            Console.WriteLine($"LogonWindow.Icon set by the app-wide style: {ok}");
            Console.WriteLine(ok ? "OK" : "FAILED");
            return;
        }

        // Dismissing the connection-lost modal signs out to the LOGON WINDOW instead of quitting the app:
        // `--connlost-signout-test`.
        //
        // A hook rather than a case in the desktop test suite because that project has no headless Avalonia at
        // all, and both halves of this are statements about a Window: which result the button closes with, and
        // what AppExceptions then does with that result. It checks the two independently, because they failed
        // independently — the button said "Close" and returned "close", and every one of the three flows threw
        // the user out of the application over a momentary network drop.
        //
        // Reverting either half makes this report FAILED, which is what says it measures the change rather
        // than some default: restore Close("close") and the first line fails; restore the Shutdown() call and
        // the second does.
        if (args.Contains("--connlost-signout-test"))
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();

            var owner = new Views.LogonWindow();
            owner.Show();

            var dialog = new Views.ConnectionLostDialog(showDetails: false, "probe");
            var closed = dialog.ShowDialog<string?>(owner);
            Dispatcher.UIThread.RunJobs();
            dialog.SignOutButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var result = closed.IsCompleted ? closed.Result : "(dialog still open)";
            var buttonOk = result == "sign-out";
            Console.WriteLine($"Second button closes with: {result} (expected sign-out) -> {buttonOk}");

            // The label has to move WITH the behaviour: "Close" on a button that reopens a sign-in window is
            // the affordance-honesty defect this change exists to fix, so a silent revert of the text is a
            // regression even while the behaviour is right.
            var label = SimplArchive.Localization.Strings.Get("ClSignOut");
            var labelOk = !string.IsNullOrWhiteSpace(label) && label != "ClSignOut" && label != "Close";
            Console.WriteLine($"Second button label: '{label}' -> {labelOk}");

            // And AppExceptions routes that result to the logon hook rather than to Shutdown().
            var returned = false;
            Services.AppExceptions.Initialize(
                owner,
                () => false,
                () => Task.CompletedTask,
                null,
                () => returned = true);
            Services.AppExceptions.ReturnToLogon();
            Console.WriteLine($"AppExceptions.ReturnToLogon invoked the logon hook: {returned}");

            var ok = buttonOk && labelOk && returned;
            Console.WriteLine(ok ? "OK" : "FAILED");
            return;
        }

        // The contents list scrolls vertically, and the filter row narrows it: `--list-scroll-test` (#48).
        // A rendered check, not a VM one — "there is a scrollbar" is a statement about the visual tree, and
        // the pane's outer ScrollViewer deliberately disables vertical (it exists to co-scroll header + rows
        // horizontally), so the vertical job belongs to the ListBox's own ScrollViewer and only a layout pass
        // can say whether it does it.
        if (args.Contains("--list-scroll-test"))
        {
            ListScrollCheck.Run();
            return;
        }

        // VM-level check that Reset restores default proportions + expands every pane, even from a fully
        // collapsed/messed-up layout: `--reset-layout-test`. (The visual re-expand needs a real desktop —
        // headless captures arrange only once.)
        if (args.Contains("--reset-layout-test"))
        {
            var vm = new MainWindowViewModel();
            vm.ToggleTreeCommand.Execute(null);
            vm.ToggleListCommand.Execute(null);
            vm.ToggleIndexCommand.Execute(null);
            vm.ToggleChatCommand.Execute(null);
            Console.WriteLine($"collapsed: tree={vm.TreeCollapsed} list={vm.ListCollapsed} index={vm.IndexCollapsed} chat={vm.ChatCollapsed}");

            vm.ResetLayoutCommand.Execute(null);
            var expanded = !vm.TreeCollapsed && !vm.ListCollapsed && !vm.IndexCollapsed && !vm.ChatCollapsed;
            // The index pane resets to AUTO, not to a proportion — it fits its content (ADR 0550), and #413
            // removed its remembered height so one drag cannot survive a collapse/expand cycle. This check
            // still asserted the old 1.5* default afterwards and had been reporting FAILED ever since (#895):
            // the behaviour changed in 684ea63f, the assertion did not. Its DefaultIndex constant is gone too,
            // which is why the 1.5 here was a bare literal with nothing left to agree with.
            var defaults = vm.TreeWidth.Value == 1.4 && vm.TreeWidth.IsStar
                && vm.ListWidth.Value == 2 && vm.ListWidth.IsStar
                && vm.IndexHeight.IsAuto
                && vm.ChatWidth.Value == 2 && vm.ChatWidth.IsStar;
            Console.WriteLine($"after reset: expanded={expanded} defaults={defaults} (tree={vm.TreeWidth} list={vm.ListWidth} index={vm.IndexHeight} chat={vm.ChatWidth})");
            Console.WriteLine(expanded && defaults ? "OK" : "FAILED");
            return;
        }

        // End-to-end check that a header edge DRAG resizes a column — the half `--columns-test` below cannot
        // reach, and the half that was broken (#786). Lives in ColumnDragCheck: this file is on the 1000-line
        // standing-debt list and may only get smaller.
        if (args.Contains("--column-drag-test"))
        {
            Views.ColumnDragCheck.Run();
            return;
        }

        // VM-level check of the contents-list column model: `--columns-test` (ADR "Desktop list-pane
        // resizable columns", reworked for #786). Covers the arithmetic only — the header edge DRAG that
        // reaches it is `--column-drag-test` above, and the two are separate because the arithmetic passed for
        // months while the drag had never once worked.
        if (args.Contains("--columns-test"))
        {
            var vm = new MainWindowViewModel();

            // A fixed column resizes itself, and clamps rather than collapsing.
            vm.ResizeColumn(2, -1000);
            var clamped = vm.ColDateWidth == 48;

            // Name is the FLEXIBLE column: given a pane, it is exactly the remainder, and the table fills the
            // pane rather than being a fixed block inside it.
            vm.ContentsPaneWidth = 1000;
            var others = vm.ContentsTotalWidth - vm.ColNameWidth;
            var fills = Math.Abs(vm.ContentsTotalWidth - 1000) < 0.001 && Math.Abs(vm.ColNameWidth - (1000 - others)) < 0.001;

            // …and below the threshold Name holds its DEFAULT width rather than the generic 48px minimum, so the
            // region overflows the pane and the horizontal scrollbar comes back. This is the half that keeps
            // "fills the pane" from meaning "unreadable when narrow": the other five columns total more than a
            // default pane is wide, so a 48px floor here collapsed Name to a stub at every ordinary width.
            vm.ContentsPaneWidth = 100;
            var scrolls = vm.ColNameWidth == 260 && vm.ContentsTotalWidth > 100;

            // Dragging NAME's edge moves width to its right neighbour: Name grows by the drag, Type gives it up,
            // and every edge further right stays put.
            vm.ContentsPaneWidth = 1000;
            var nameBefore = vm.ColNameWidth;
            var typeBefore = vm.ColTypeWidth;
            vm.ResizeColumn(0, 40);
            var neighbour = Math.Abs(vm.ColTypeWidth - (typeBefore - 40)) < 0.001
                && Math.Abs(vm.ColNameWidth - (nameBefore + 40)) < 0.001
                && Math.Abs(vm.ContentsTotalWidth - 1000) < 0.001;   // the total is unchanged: width MOVED

            vm.SaveLayout();
            var reloaded = new MainWindowViewModel();
            var persisted = reloaded.ColTypeWidth == typeBefore - 40 && reloaded.ColDateWidth == 48;
            reloaded.ResetLayoutCommand.Execute(null); // leave defaults behind
            var reset = reloaded.ColTypeWidth == 130 && reloaded.ColDateWidth == 96;

            Console.WriteLine($"clamped={clamped} fills={fills} scrolls={scrolls} neighbour={neighbour} persisted={persisted} reset={reset}");
            Console.WriteLine(clamped && fills && scrolls && neighbour && persisted && reset ? "OK" : "FAILED");
            return;
        }

        // Proves the date fields' binding round-trips (ADR "desktop date fields"): `--datepicker-test`.
        // Its own class, not another inline block — this file is on the 1000-line list precisely because all
        // 28 hooks live in it, and the guard's advice is to give new code a home rather than raise the number.
        if (args.Contains("--datepicker-test"))
        {
            DatePickerBindingCheck.Run();
            return;
        }

        // VM-level check that each Intray pane collapses to height 0 and the state round-trips through the
        // persisted layout: `--intray-collapse-test` (ADR "Collapsible inbox panes").
        if (args.Contains("--intray-collapse-test"))
        {
            var vm = new MainWindowViewModel();
            vm.Intray.ToggleServerCommand.Execute(null);
            vm.Intray.ToggleLocalCommand.Execute(null);
            vm.Intray.ToggleMaskCommand.Execute(null);
            vm.Intray.TogglePreviewCommand.Execute(null);
            var collapsedToZero = vm.Intray.ServerHeight.Value == 0 && vm.Intray.LocalHeight.Value == 0
                && vm.Intray.MaskHeight.Value == 0 && vm.Intray.PreviewHeight.Value == 0;
            var flags = vm.Intray.ServerCollapsed && vm.Intray.LocalCollapsed && vm.Intray.MaskCollapsed && vm.Intray.PreviewCollapsed;
            Console.WriteLine($"collapsed: heights0={collapsedToZero} flags={flags}");

            // A fresh VM loads the just-persisted state (all collapsed).
            var reloaded = new MainWindowViewModel();
            var persisted = reloaded.Intray.ServerCollapsed && reloaded.Intray.LocalCollapsed
                && reloaded.Intray.MaskCollapsed && reloaded.Intray.PreviewCollapsed;
            reloaded.ResetLayoutCommand.Execute(null); // restore defaults so the test leaves no collapsed state behind
            var reset = !reloaded.Intray.ServerCollapsed && reloaded.Intray.MaskHeight.Value == 1.1 && reloaded.Intray.MaskHeight.IsStar;
            Console.WriteLine($"reloaded persisted={persisted} | reset ok={reset}");
            Console.WriteLine(collapsedToZero && flags && persisted && reset ? "OK" : "FAILED");
            return;
        }

        // Pure-logic check of inserting a clicked preview word into a focused text field (ADR "Intray
        // refinements"): `--intray-insert-test`. The focus capture + wiring need a real desktop.
        if (args.Contains("--intray-insert-test"))
        {
            // caret insert (no selection): "ab|cd" + "X" -> "abXcd", caret after X
            var a = Views.HighlightOverlayDrawing.InsertWordInto("abcd", 2, 2, "X", append: false);
            // shift prepends a space after a non-space: "ab|" + "X" -> "ab X"
            var b = Views.HighlightOverlayDrawing.InsertWordInto("ab", 2, 2, "X", append: true);
            // shift at start: no leading space
            var c = Views.HighlightOverlayDrawing.InsertWordInto("", 0, 0, "X", append: true);
            // replaces a selection: "abcd" with [1,3) selected + "X" -> "aXd"
            var d = Views.HighlightOverlayDrawing.InsertWordInto("abcd", 1, 3, "X", append: false);
            var ok = a == ("abXcd", 3) && b == ("ab X", 4) && c == ("X", 1) && d == ("aXd", 2);
            Console.WriteLine($"insert: caret={a} shiftSpace={b} shiftStart={c} replaceSel={d}");
            Console.WriteLine(ok ? "OK" : "FAILED");
            return;
        }

        // Pure-logic check of the annotation save-guard (ADR "Annotation shape recolour"): a sticky note needs
        // text, but a markup shape's text is optional so a colour-only save is valid: `--annotation-save-test`.
        if (args.Contains("--annotation-save-test"))
        {
            var noteEmpty = Views.AnnotationDialog.CanSave("", isShape: false);  // false — a note needs text
            var noteText = Views.AnnotationDialog.CanSave("hi", isShape: false);  // true
            var shapeEmpty = Views.AnnotationDialog.CanSave("", isShape: true);   // true — the fix (recolour a highlight)
            Console.WriteLine($"note-empty={noteEmpty} note-text={noteText} shape-empty={shapeEmpty}");
            Console.WriteLine(!noteEmpty && noteText && shapeEmpty ? "OK" : "FAILED");
            return;
        }

        // The standalone-window screenshot hooks live in WindowShots (issue #466): each renders one dialog or
        // window headlessly to PNG, and together they were a quarter of this switchboard.
        if (Views.WindowShots.TryRun(args))
        {
            return;
        }

        // The icon hooks (ADR 0578): `--gen-icons [dir]` writes every launcher artefact from the one piece of
        // art, `--gen-icon <out.png>` renders that art alone. Both need Skia, hence the shared setup; the
        // dispatch and the argument walk belong to IconWriter, which is the thing that knows about icons.
        var genIconsIndex = Array.IndexOf(args, "--gen-icons");
        var genIconIndex = Array.IndexOf(args, "--gen-icon");
        if (genIconsIndex >= 0 || (genIconIndex >= 0 && genIconIndex + 1 < args.Length))
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();

            Services.IconWriter.Run(args, genIconsIndex, genIconIndex);
            return;
        }

        // Headless render-to-PNG mode for verification without a display: `--screenshot <path>`.
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
        {
            var pdfIndex = Array.IndexOf(args, "--pdf");
            var pdfPath = pdfIndex >= 0 && pdfIndex + 1 < args.Length ? args[pdfIndex + 1] : null;
            Views.ScreenshotRenderer.Render(args[screenshotIndex + 1], demo: args.Contains("--demo"), pdfPath);
            return;
        }

        // Headless test of the PDF->bitmap preview pipeline (Docnet/PDFium -> Avalonia): `--render-pdf <in.pdf> <out.png>`.
        var renderPdfIndex = Array.IndexOf(args, "--render-pdf");
        if (renderPdfIndex >= 0 && renderPdfIndex + 2 < args.Length)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
            var pages = Services.PreviewRenderer.RenderPdfPages(File.ReadAllBytes(args[renderPdfIndex + 1]));
            var outArg = args[renderPdfIndex + 2];
            for (var i = 0; i < pages.Count; i++)
            {
                var path = pages.Count == 1 ? outArg : Path.Combine(Path.GetDirectoryName(outArg) ?? ".", $"{Path.GetFileNameWithoutExtension(outArg)}-{i + 1}{Path.GetExtension(outArg)}");
                pages[i].Save(path);
                Console.WriteLine($"page {i + 1}/{pages.Count} -> {path} ({pages[i].PixelSize})");
            }

            return;
        }

        // Regression guard for the PDF preview background (ADR "Desktop PDF preview white background"):
        // `--pdf-opaque-test`. A PDF that draws no page background must still render as opaque white paper, not a
        // transparent page that shows the dark surface behind it (the "black bars on a datasheet" bug). Renders a
        // minimal no-background PDF and asserts every pixel is fully opaque.
        if (args.Contains("--pdf-opaque-test"))
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
            var pdf = System.Text.Encoding.ASCII.GetBytes(
                "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
                "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 600 800]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
                "4 0 obj<</Length 46>>stream\nBT /F1 30 Tf 80 700 Td (No background) Tj ET\nendstream endobj\n" +
                "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R/Size 6>>\n%%EOF");
            // Read the pixels back through CopyPixels rather than by casting to WriteableBitmap and calling
            // Lock(). The cast is what silently disabled this guard (#925): RenderPdfFirstPage has always
            // DECLARED Bitmap, but returned a WriteableBitmap until #522 made it construct an immutable one on
            // purpose (Skia's ResizeBitmap accepts only immutable sources). Legal against the declared type, so
            // the compiler said nothing, and a correct change turned off the check for a different bug (#196).
            // CopyPixels asks only for what Bitmap itself promises, so narrowing the concrete type cannot
            // break it again.
            var bmp = Services.PreviewRenderer.RenderPdfFirstPage(pdf);
            var stride = bmp.PixelSize.Width * 4;
            var bytes = new byte[stride * bmp.PixelSize.Height];
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(default, bmp.PixelSize), pin.AddrOfPinnedObject(), bytes.Length, stride);
            }
            finally
            {
                pin.Free();
            }

            var transparent = 0;
            for (var y = 0; y < bmp.PixelSize.Height; y++)
            {
                for (var x = 0; x < bmp.PixelSize.Width; x++)
                {
                    if (bytes[y * stride + x * 4 + 3] != 255) { transparent++; }
                }
            }

            Console.WriteLine($"transparent pixels: {transparent}");
            Console.WriteLine(transparent == 0 ? "OK" : "FAILED");
            return;
        }

        // The clearable search field (#503): `--searchclear-test`. Its body lives in SearchFieldCheck — this
        // file is on the 1000-line standing-debt list (issue #466) and may only get smaller, and a verification
        // routine is exactly the kind of self-contained thing that has no business growing here.
        if (args.Contains("--searchclear-test"))
        {
            SearchFieldCheck.Run();
            return;
        }

        // The side-by-side diff renders (#803, ADR 0712): `--diff-test`, in DiffViewCheck — headless
        // Avalonia, because the point is that the rows MATERIALIZE (a VM test cannot see an empty panel).
        if (args.Contains("--diff-test"))
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
            Services.DiffViewCheck.Run();
            return;
        }

        // The Open shortcut (#482, ADR "One shortcut for opening a document"): `--shortcut-test`, in
        // OpenShortcutCheck.
        if (args.Contains("--shortcut-test"))
        {
            OpenShortcutCheck.Run();
            return;
        }

        // The sort dialog's page pictures against the running Api (#522): `--sort-thumbs-test <token> <name>`,
        // in SortThumbnailsCheck — headless Avalonia, because the pipeline decodes into real bitmaps.
        var sortThumbsIndex = Array.IndexOf(args, "--sort-thumbs-test");
        if (sortThumbsIndex >= 0 && sortThumbsIndex + 3 < args.Length)
        {
            DesktopClientOptions.ApiBaseUrl = args[sortThumbsIndex + 3].TrimEnd('/');
            var ok = SortThumbnailsCheck.RunAsync(args[sortThumbsIndex + 1], args[sortThumbsIndex + 2]);
            Environment.Exit(ok ? 0 : 1);
        }

        // Preview zoom over the REAL raster path (#480, ADR "Fit the whole page"): `--zoom-test`. Rasterises a
        // portrait A4-ish page through PDFium, hands it to a PreviewViewModel with a pane WIDER than it is tall —
        // the case fit-width cannot serve — and checks the whole model: fit-width is the default, fit-page lands
        // below 1 with the page's full height inside the pane, zooming out then stops at whole-page instead of at
        // fit-width, zooming in stops at the ceiling, and a new document goes back to fit-width.
        if (args.Contains("--zoom-test"))
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
            var pdf = System.Text.Encoding.ASCII.GetBytes(
                "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
                "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R>>endobj\n" +
                "4 0 obj<</Length 0>>stream\nendstream endobj\ntrailer<</Root 1 0 R/Size 5>>\n%%EOF");
            var vm = new PreviewViewModel(new NoStatusLine());
            vm.SetPreviewPagesForScreenshot([Services.PreviewRenderer.RenderPdfFirstPage(pdf)]);

            const double paneWidth = 900, paneHeight = 520;   // wider than tall — a portrait page cannot fit by width
            vm.SetViewport(new Avalonia.Size(paneWidth, paneHeight));
            var pageBase = paneWidth - 12;
            var aspect = vm.PreviewPages[0].Image.Size.Height / vm.PreviewPages[0].Image.Size.Width;

            var fitsWidthByDefault = vm.Zoom == 1 && Math.Abs(vm.PageWidth - pageBase) < 0.001;
            var tooTallByWidth = pageBase * aspect > paneHeight; // the bug: the bottom is off screen at fit-width

            vm.FitPageCommand.Execute(null);
            var fitPageIsBelowOne = vm.Zoom < 1;
            var wholePageVisible = vm.PageWidth * aspect <= paneHeight;
            var fitPageZoom = vm.Zoom;

            vm.ZoomOutCommand.Execute(null);
            var outStopsAtWholePage = Math.Abs(vm.Zoom - fitPageZoom) < 0.001;

            for (var i = 0; i < 20; i++) { vm.ZoomInCommand.Execute(null); }
            var inStopsAtCeiling = Math.Abs(vm.Zoom - 4) < 0.001;

            vm.Reset(null);
            vm.SetPreviewPagesForScreenshot([Services.PreviewRenderer.RenderPdfFirstPage(pdf)]);
            vm.ZoomOutCommand.Execute(null);
            var newDocumentOpensAtFitWidth = vm.Zoom == 1;

            Console.WriteLine($"at fit-width the page is {vm.PageWidth:0.#}x{vm.PageWidth * aspect:0.#} in a {paneWidth}x{paneHeight} pane; fit-page zoom {fitPageZoom:0.###}");
            Console.WriteLine($"fitsWidthByDefault={fitsWidthByDefault} tooTallByWidth={tooTallByWidth} fitPageIsBelowOne={fitPageIsBelowOne} "
                + $"wholePageVisible={wholePageVisible} outStopsAtWholePage={outStopsAtWholePage} inStopsAtCeiling={inStopsAtCeiling} "
                + $"newDocumentOpensAtFitWidth={newDocumentOpensAtFitWidth}");
            Console.WriteLine(fitsWidthByDefault && tooTallByWidth && fitPageIsBelowOne && wholePageVisible
                && outStopsAtWholePage && inStopsAtCeiling && newDocumentOpensAtFitWidth ? "OK" : "FAILED");
            return;
        }

        // Headless test of the new-folder flow against a running Api: `--newfolder-test <token> <name>`.
        var newFolderIndex = Array.IndexOf(args, "--newfolder-test");
        if (newFolderIndex >= 0 && newFolderIndex + 2 < args.Length)
        {
            Services.ApiClientChecks.NewFolderAsync(args[newFolderIndex + 1], args[newFolderIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the rename/delete/recycle-bin/restore flow against a running Api:
        // `--modify-test <token>`.
        var modifyIndex = Array.IndexOf(args, "--modify-test");
        if (modifyIndex >= 0 && modifyIndex + 1 < args.Length)
        {
            Services.ApiClientChecks.ModifyAsync(args[modifyIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the Save-as data path (resolve URL -> download -> write file, minus the native
        // picker) against a running Api: `--saveas-test <token> <outPath>`.
        var saveAsIndex = Array.IndexOf(args, "--saveas-test");
        if (saveAsIndex >= 0 && saveAsIndex + 2 < args.Length)
        {
            Services.ApiClientChecks.SaveAsAsync(args[saveAsIndex + 1], args[saveAsIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the move/reference/go-to/remove flow (the desktop API-client methods the drag-drop
        // UI calls) against a running Api: `--reference-test <token>`.
        var referenceIndex = Array.IndexOf(args, "--reference-test");
        if (referenceIndex >= 0 && referenceIndex + 1 < args.Length)
        {
            Services.ApiClientChecks.ReferenceAsync(args[referenceIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of metadata search against a running Api: `--search-test <token> <query>`.
        var searchIndex = Array.IndexOf(args, "--search-test");
        if (searchIndex >= 0 && searchIndex + 2 < args.Length)
        {
            Services.ApiClientChecks.SearchAsync(args[searchIndex + 1], args[searchIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the references-of-an-item flow (GetReferencingFoldersAsync + hasReferences) against
        // a running Api: `--referencing-test <token>`.
        var referencingIndex = Array.IndexOf(args, "--referencing-test");
        if (referencingIndex >= 0 && referencingIndex + 1 < args.Length)
        {
            Services.ApiClientChecks.ReferencingAsync(args[referencingIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the upload flow against a running Api: `--upload-test <token> <filePath>`.
        var uploadIndex = Array.IndexOf(args, "--upload-test");
        if (uploadIndex >= 0 && uploadIndex + 2 < args.Length)
        {
            Services.ApiClientChecks.UploadAsync(args[uploadIndex + 1], args[uploadIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of breadcrumb navigation against a running Api: `--breadcrumb-test <token>`.
        var breadcrumbIndex = Array.IndexOf(args, "--breadcrumb-test");
        if (breadcrumbIndex >= 0 && breadcrumbIndex + 1 < args.Length)
        {
            var trail = new MainWindowViewModel().BreadcrumbSelfTestAsync(args[breadcrumbIndex + 1]).GetAwaiter().GetResult();
            foreach (var step in trail)
            {
                Console.WriteLine(step);
            }

            return;
        }

        // Headless test that a referenced folder appears in the tree: `--reftree-test <token>`.
        var refTreeIndex = Array.IndexOf(args, "--reftree-test");
        if (refTreeIndex >= 0 && refTreeIndex + 1 < args.Length)
        {
            foreach (var line in new MainWindowViewModel().RefTreeSelfTestAsync(args[refTreeIndex + 1]).GetAwaiter().GetResult())
            {
                Console.WriteLine(line);
            }

            return;
        }

        // Headless test that the folders-only tree refreshes after a new folder / on Refresh:
        // `--treerefresh-test <token>`.
        var treeRefreshIndex = Array.IndexOf(args, "--treerefresh-test");
        if (treeRefreshIndex >= 0 && treeRefreshIndex + 1 < args.Length)
        {
            foreach (var line in new MainWindowViewModel().TreeRefreshSelfTestAsync(args[treeRefreshIndex + 1]).GetAwaiter().GetResult())
            {
                Console.WriteLine(line);
            }

            return;
        }

        // Headless self-test of the browse/download code paths against the running Api, given an access token
        // obtained out of band: `--selftest <accessToken>`. Exercises the same SimplArchiveApiClient /
        // NativeFileOpener the UI uses.
        var selfTestIndex = Array.IndexOf(args, "--selftest");
        if (selfTestIndex >= 0 && selfTestIndex + 1 < args.Length)
        {
            Services.ApiClientChecks.SelfAsync(args[selfTestIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // TEMP: exercise the workflow api-client path (create doc → submit → approve → release) against a
        // running Api: `--workflow-test <token>`.
        var wfTestIndex = Array.IndexOf(args, "--workflow-test");
        if (wfTestIndex >= 0 && wfTestIndex + 1 < args.Length)
        {
            Services.ApiClientChecks.WorkflowAsync(args[wfTestIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Check that the desktop client resolves a multi-page TIFF into separate page images (ADR "Multi-page
        // TIFF preview pages"): `--multipage-test <token> <documentName>`. Exercises the real api-client parse +
        // download; skips Avalonia image decoding (that needs a display). Takes the NAME (searched across the
        // visible repositories' children) — an id alone has no address any more (#443).
        var multipageIndex = Array.IndexOf(args, "--multipage-test");
        if (multipageIndex >= 0 && multipageIndex + 2 < args.Length)
        {
            Services.ApiClientChecks.MultipageAsync(args[multipageIndex + 1], args[multipageIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless check of the hit-word geometry (ADR "Copy a preview word to the clipboard") — clicking is interactive,
        // but which word a click resolves to is pure geometry: `--hitcopy-test`.
        if (args.Contains("--hitcopy-test"))
        {
            RunHitCopyTest();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunHitCopyTest()
    {
        var boxes = new List<ViewModels.HighlightBox>
        {
            new("RECHNUNG", 0.0831, 0.2668, 0.2395, 0.0257),
            new("Xylophonkatze", 0.2379, 0.3854, 0.1492, 0.0160),
        };

        // A page rendered at 1000x1400; probe a point inside each box and one outside.
        const double w = 1000, h = 1400;
        (string label, Avalonia.Point p)[] probes =
        {
            ("centre of RECHNUNG", new Avalonia.Point((0.0831 + 0.2395 / 2) * w, (0.2668 + 0.0257 / 2) * h)),
            ("centre of Xylophonkatze", new Avalonia.Point((0.2379 + 0.1492 / 2) * w, (0.3854 + 0.0160 / 2) * h)),
            ("empty area", new Avalonia.Point(0.9 * w, 0.9 * h)),
        };

        foreach (var (label, p) in probes)
        {
            var hit = Views.HighlightOverlayDrawing.HitTest(boxes, p, w, h);
            Console.WriteLine($"{label}: {hit?.Text ?? "(none)"}");
        }
    }

    // Referenced by the Avalonia design-time tooling too.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();


}
