using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

        // The log comes up before anything that might want to write to it (ADR 0613).
        Services.DesktopLog.Initialize();

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
            var defaults = vm.TreeWidth.Value == 1.4 && vm.TreeWidth.IsStar
                && vm.ListWidth.Value == 2 && vm.ListWidth.IsStar
                && vm.IndexHeight.Value == 1.5 && vm.IndexHeight.IsStar
                && vm.ChatWidth.Value == 2 && vm.ChatWidth.IsStar;
            Console.WriteLine($"after reset: expanded={expanded} defaults={defaults} (tree={vm.TreeWidth} list={vm.ListWidth} index={vm.IndexHeight} chat={vm.ChatWidth})");
            Console.WriteLine(expanded && defaults ? "OK" : "FAILED");
            return;
        }

        // VM-level check that the contents-list columns resize (clamped to a minimum), the total width sums,
        // the widths round-trip through the persisted layout, and Reset restores defaults: `--columns-test`
        // (ADR "Desktop list-pane resizable columns"). The header Thumb drag itself needs a real desktop.
        if (args.Contains("--columns-test"))
        {
            var vm = new MainWindowViewModel();
            var total0 = vm.ContentsTotalWidth;
            vm.ResizeColumn(0, 40);            // widen Name
            vm.ResizeColumn(2, -1000);         // shrink Date past the minimum → clamps
            var widened = vm.ColNameWidth == 300;
            var clamped = vm.ColDateWidth == 48;
            var totalOk = Math.Abs(vm.ContentsTotalWidth - (total0 + 40 - (96 - 48))) < 0.001;
            vm.SaveLayout();

            var reloaded = new MainWindowViewModel();
            var persisted = reloaded.ColNameWidth == 300 && reloaded.ColDateWidth == 48;
            reloaded.ResetLayoutCommand.Execute(null); // leave defaults behind
            var reset = reloaded.ColNameWidth == 260 && reloaded.ColDateWidth == 96;
            Console.WriteLine($"widened={widened} clamped={clamped} totalOk={totalOk} persisted={persisted} reset={reset}");
            Console.WriteLine(widened && clamped && totalOk && persisted && reset ? "OK" : "FAILED");
            return;
        }

        // VM-level check that each Intray pane collapses to height 0 and the state round-trips through the
        // persisted layout: `--intray-collapse-test` (ADR "Collapsible inbox panes").
        if (args.Contains("--intray-collapse-test"))
        {
            var vm = new MainWindowViewModel();
            vm.ToggleIntrayServerCommand.Execute(null);
            vm.ToggleIntrayLocalCommand.Execute(null);
            vm.ToggleIntrayMaskCommand.Execute(null);
            vm.ToggleIntrayPreviewCommand.Execute(null);
            var collapsedToZero = vm.IntrayServerHeight.Value == 0 && vm.IntrayLocalHeight.Value == 0
                && vm.IntrayMaskHeight.Value == 0 && vm.IntrayPreviewHeight.Value == 0;
            var flags = vm.IntrayServerCollapsed && vm.IntrayLocalCollapsed && vm.IntrayMaskCollapsed && vm.IntrayPreviewCollapsed;
            Console.WriteLine($"collapsed: heights0={collapsedToZero} flags={flags}");

            // A fresh VM loads the just-persisted state (all collapsed).
            var reloaded = new MainWindowViewModel();
            var persisted = reloaded.IntrayServerCollapsed && reloaded.IntrayLocalCollapsed
                && reloaded.IntrayMaskCollapsed && reloaded.IntrayPreviewCollapsed;
            reloaded.ResetLayoutCommand.Execute(null); // restore defaults so the test leaves no collapsed state behind
            var reset = !reloaded.IntrayServerCollapsed && reloaded.IntrayMaskHeight.Value == 1.1 && reloaded.IntrayMaskHeight.IsStar;
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
            RenderScreenshot(args[screenshotIndex + 1], demo: args.Contains("--demo"), pdfPath);
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
            var bmp = (Avalonia.Media.Imaging.WriteableBitmap)Services.PreviewRenderer.RenderPdfFirstPage(pdf);
            using var fb = bmp.Lock();
            var bytes = new byte[fb.RowBytes * bmp.PixelSize.Height];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
            var transparent = 0;
            for (var y = 0; y < bmp.PixelSize.Height; y++)
            {
                for (var x = 0; x < bmp.PixelSize.Width; x++)
                {
                    if (bytes[y * fb.RowBytes + x * 4 + 3] != 255) { transparent++; }
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
            var vm = new PreviewViewModel();
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
            RunNewFolderTestAsync(args[newFolderIndex + 1], args[newFolderIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the rename/delete/recycle-bin/restore flow against a running Api:
        // `--modify-test <token>`.
        var modifyIndex = Array.IndexOf(args, "--modify-test");
        if (modifyIndex >= 0 && modifyIndex + 1 < args.Length)
        {
            RunModifyTestAsync(args[modifyIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the Save-as data path (resolve URL -> download -> write file, minus the native
        // picker) against a running Api: `--saveas-test <token> <outPath>`.
        var saveAsIndex = Array.IndexOf(args, "--saveas-test");
        if (saveAsIndex >= 0 && saveAsIndex + 2 < args.Length)
        {
            RunSaveAsTestAsync(args[saveAsIndex + 1], args[saveAsIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the move/reference/go-to/remove flow (the desktop API-client methods the drag-drop
        // UI calls) against a running Api: `--reference-test <token>`.
        var referenceIndex = Array.IndexOf(args, "--reference-test");
        if (referenceIndex >= 0 && referenceIndex + 1 < args.Length)
        {
            RunReferenceTestAsync(args[referenceIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of metadata search against a running Api: `--search-test <token> <query>`.
        var searchIndex = Array.IndexOf(args, "--search-test");
        if (searchIndex >= 0 && searchIndex + 2 < args.Length)
        {
            RunSearchTestAsync(args[searchIndex + 1], args[searchIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the references-of-an-item flow (GetReferencingFoldersAsync + hasReferences) against
        // a running Api: `--referencing-test <token>`.
        var referencingIndex = Array.IndexOf(args, "--referencing-test");
        if (referencingIndex >= 0 && referencingIndex + 1 < args.Length)
        {
            RunReferencingTestAsync(args[referencingIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Headless test of the upload flow against a running Api: `--upload-test <token> <filePath>`.
        var uploadIndex = Array.IndexOf(args, "--upload-test");
        if (uploadIndex >= 0 && uploadIndex + 2 < args.Length)
        {
            RunUploadTestAsync(args[uploadIndex + 1], args[uploadIndex + 2]).GetAwaiter().GetResult();
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
            RunSelfTestAsync(args[selfTestIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // TEMP: exercise the workflow api-client path (create doc → submit → approve → release) against a
        // running Api: `--workflow-test <token>`.
        var wfTestIndex = Array.IndexOf(args, "--workflow-test");
        if (wfTestIndex >= 0 && wfTestIndex + 1 < args.Length)
        {
            RunWorkflowTestAsync(args[wfTestIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        // Check that the desktop client resolves a multi-page TIFF into separate page images (ADR "Multi-page
        // TIFF preview pages"): `--multipage-test <token> <documentName>`. Exercises the real api-client parse +
        // download; skips Avalonia image decoding (that needs a display). Takes the NAME (searched across the
        // visible repositories' children) — an id alone has no address any more (#443).
        var multipageIndex = Array.IndexOf(args, "--multipage-test");
        if (multipageIndex >= 0 && multipageIndex + 2 < args.Length)
        {
            RunMultipageTestAsync(args[multipageIndex + 1], args[multipageIndex + 2]).GetAwaiter().GetResult();
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

    private static async Task RunMultipageTestAsync(string token, string documentName)
    {
        var api = new Services.SimplArchiveApiClient(token);
        var document = (await api.Documents.GetRepositoriesAsync())
            .SelectMany(r => api.Documents.GetChildrenAsync(r.Href("children")).GetAwaiter().GetResult())
            .FirstOrDefault(c => c.Name == documentName)
            ?? throw new InvalidOperationException($"No document named '{documentName}' in any visible repository's top level.");
        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        Console.WriteLine($"preview-pages link present: {preview.PreviewPagesUrl is not null}");
        if (preview.PreviewPagesUrl is not { } url)
        {
            Console.WriteLine("FAILED: no preview-pages link.");
            return;
        }

        var pages = await api.Versions.GetPreviewPagesAsync(url);
        Console.WriteLine($"page urls: {pages?.Count ?? 0}");
        if (pages is null)
        {
            Console.WriteLine("FAILED: preview-pages returned null.");
            return;
        }

        var i = 0;
        foreach (var pageUrl in pages)
        {
            var (bytes, _) = await Services.SimplArchiveApiClient.DownloadAsync(pageUrl);
            // PNG IHDR: width/height at bytes 16..24 (big-endian) — validates it's a real page image.
            var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            Console.WriteLine($"  page {++i}: {w}x{h} ({bytes.Length} bytes)");
        }

        Console.WriteLine(i > 1 ? "OK: multiple pages fetched." : "FAILED: expected multiple pages.");
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

    private static void RenderScreenshot(string path, bool demo, string? pdfPath = null)
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
                PopulateHitOverlayScreenshot(viewModel);
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
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // `--fit-page`: the zoom that fits the whole page (#480), applied AFTER the first arrange because it is
        // measured from the pane the preview actually got.
        if (Environment.GetCommandLineArgs().Contains("--fit-page"))
        {
            viewModel.Preview.FitPageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
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
    private static void PopulateHitOverlayScreenshot(MainWindowViewModel viewModel)
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

    private static async Task RunNewFolderTestAsync(string accessToken, string name)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        Console.WriteLine($"creating folder '{name}' in '{root.Name}'…");

        await api.Documents.CreateFolderAsync(root.Href("children"), name);

        var match = (await api.Documents.GetChildrenAsync(root.Href("children"))).FirstOrDefault(c => c.Name == name);
        Console.WriteLine(match is null
            ? "FAILED: folder not found."
            : $"OK: '{match.Name}' present, isFolder={!match.HasVersions}");
    }

    private static async Task RunModifyTestAsync(string accessToken)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();

        var original = $"modify-test-{Guid.NewGuid():N}";
        var renamed = $"{original}-renamed";
        Console.WriteLine($"creating folder '{original}' in '{root.Name}'…");
        await api.Documents.CreateFolderAsync(root.Href("children"), original);
        var created = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == original);

        Console.WriteLine($"renaming to '{renamed}'…");
        await api.Documents.RenameAsync(created.Href("self"), renamed);
        var afterRename = await api.Documents.GetChildrenAsync(root.Href("children"));
        Console.WriteLine(afterRename.Any(c => c.Name == renamed) && afterRename.All(c => c.Name != original)
            ? "OK: rename reflected."
            : "FAILED: rename not reflected.");

        Console.WriteLine("deleting…");
        await api.Documents.DeleteAsync(created.Href("self"));
        var afterDelete = await api.Documents.GetChildrenAsync(root.Href("children"));
        var recycled = await api.Documents.GetRecycleBinAsync(root);
        Console.WriteLine(afterDelete.All(c => c.Id != created.Id) && recycled.Any(r => r.Id == created.Id)
            ? "OK: gone from folder, present in recycle bin."
            : "FAILED: delete/recycle-bin state wrong.");

        Console.WriteLine("restoring…");
        await api.Documents.RestoreAsync(recycled.Single(r => r.Id == created.Id));
        var afterRestore = await api.Documents.GetChildrenAsync(root.Href("children"));
        var recycledAfter = await api.Documents.GetRecycleBinAsync(root);
        Console.WriteLine(afterRestore.Any(c => c.Id == created.Id) && recycledAfter.All(r => r.Id != created.Id)
            ? "OK: restored to folder, cleared from recycle bin."
            : "FAILED: restore state wrong.");

        // Clean up so repeated runs don't accumulate folders.
        await api.Documents.DeleteAsync(created.Href("self"));
    }

    private static async Task RunSaveAsTestAsync(string accessToken, string outPath)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();

        var name = $"saveas-test-{Guid.NewGuid():N}.txt";
        var content = System.Text.Encoding.UTF8.GetBytes("save-as round-trip test\n");
        Console.WriteLine($"uploading '{name}' to '{root.Name}'…");
        await api.Documents.UploadFileAsync(root.Href("children"), name, content);
        var document = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == name);

        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        if (preview.DownloadUrl is null)
        {
            Console.WriteLine("FAILED: no download URL.");
            return;
        }

        var (bytes, _) = await Services.SimplArchiveApiClient.DownloadAsync(preview.DownloadUrl);
        await File.WriteAllBytesAsync(outPath, bytes);
        Console.WriteLine(bytes.SequenceEqual(content)
            ? $"OK: saved {bytes.Length} bytes -> {outPath}; round-trip matches."
            : "FAILED: saved bytes don't match the uploaded content.");

        await api.Documents.DeleteAsync(document.Href("self")); // cleanup
    }

    private static async Task RunReferenceTestAsync(string accessToken)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        var s = Guid.NewGuid().ToString("N")[..6];

        await api.Documents.CreateFolderAsync(root.Href("children"), $"ref-A-{s}");
        await api.Documents.CreateFolderAsync(root.Href("children"), $"ref-B-{s}");
        var a = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"ref-A-{s}");
        var b = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"ref-B-{s}");
        await api.Documents.CreateFolderAsync(a.Href("children"), $"ref-C-{s}");
        var c = (await api.Documents.GetChildrenAsync(a.Href("children"))).First(n => n.Name == $"ref-C-{s}");

        Console.WriteLine("moving C from A to B…");
        await api.Documents.MoveAsync(c.Href("self"), b.Id);
        var cInB = (await api.Documents.GetChildrenAsync(b.Href("children"))).Any(n => n.Id == c.Id);
        var cGoneFromA = !(await api.Documents.GetChildrenAsync(a.Href("children"))).Any(n => n.Id == c.Id);
        Console.WriteLine(cInB && cGoneFromA ? "OK: moved." : "FAILED: move state wrong.");

        Console.WriteLine("referencing C into A…");
        await api.Documents.CreateReferenceAsync(a.Href("references"), c.Id);
        var refs = await api.Documents.GetReferencesAsync(a.Href("references"));
        var reference = refs.FirstOrDefault(r => r.TargetId == c.Id);
        Console.WriteLine(reference is not null && reference.RealParentId == b.Id
            ? $"OK: reference present, realParentId points to B; go-to folder = '{(await api.GetDocumentByAddressAsync(reference.Links!["go-to"])).Name}'."
            : "FAILED: reference/realParentId wrong.");

        Console.WriteLine("removing the reference…");
        await api.Documents.DeleteReferenceAsync(reference!.DeleteHref!);
        Console.WriteLine((await api.Documents.GetReferencesAsync(a.Href("references"))).Count == 0 ? "OK: reference removed." : "FAILED: reference still present.");

        await api.Documents.DeleteAsync(a.Href("self")); // cleanup (cascades C)
        await api.Documents.DeleteAsync(b.Href("self"));
    }

    private static async Task RunSearchTestAsync(string accessToken, string query)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        Console.WriteLine($"searching for '{query}'…");
        var results = await api.Search.SearchAsync(query);
        Console.WriteLine($"{results.Count} result(s):");
        foreach (var result in results)
        {
            Console.WriteLine($"  {(result.IsFolder ? "[folder]" : "[doc]   ")} {result.Name}   —   {result.Path}");
        }
    }

    private static async Task RunReferencingTestAsync(string accessToken)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        var s = Guid.NewGuid().ToString("N")[..6];

        await api.Documents.CreateFolderAsync(root.Href("children"), $"rt-A-{s}");
        await api.Documents.CreateFolderAsync(root.Href("children"), $"rt-B-{s}");
        var a = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rt-A-{s}");
        var b = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rt-B-{s}");
        await api.Documents.CreateFolderAsync(a.Href("children"), $"rt-C-{s}");
        var c = (await api.Documents.GetChildrenAsync(a.Href("children"))).First(n => n.Name == $"rt-C-{s}");

        await api.Documents.CreateReferenceAsync(b.Href("references"), c.Id);

        var cRow = (await api.Documents.GetChildrenAsync(a.Href("children"))).First(n => n.Id == c.Id);
        Console.WriteLine(cRow.HasReferences ? "OK: hasReferences=true on the referenced item." : "FAILED: hasReferences not set.");

        var folders = await api.Documents.GetReferencingFoldersAsync(c.Href("referencing-folders"));
        var match = folders.FirstOrDefault(f => f.Id == b.Id);
        Console.WriteLine(match is not null
            ? $"OK: referencing folder listed with path '{match.Path}'."
            : "FAILED: referencing folder not listed.");

        await api.Documents.DeleteAsync(a.Href("self"));
        await api.Documents.DeleteAsync(b.Href("self"));
    }

    private static async Task RunUploadTestAsync(string accessToken, string filePath)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        var name = Path.GetFileName(filePath);
        Console.WriteLine($"uploading '{name}' into '{root.Name}'…");

        await api.Documents.UploadFileAsync(root.Href("children"), name, await File.ReadAllBytesAsync(filePath));

        var match = (await api.Documents.GetChildrenAsync(root.Href("children"))).FirstOrDefault(c => c.Name == name);
        Console.WriteLine(match is null
            ? "FAILED: uploaded document not found in the folder."
            : $"OK: '{match.Name}' present, hasVersions={match.HasVersions}");
    }

    private static async Task RunWorkflowTestAsync(string accessToken)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);
        var me = await api.GetWhoAmIAsync();
        var repo = (await api.Documents.GetRepositoriesAsync()).First();
        Console.WriteLine($"repo '{repo.Name}', me {me.UserId}");

        await api.Documents.UploadFileAsync(repo.Href("children"), "wf-desktop-test.txt", System.Text.Encoding.UTF8.GetBytes("workflow desktop test"));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == "wf-desktop-test");
        Console.WriteLine($"created doc {doc.Name} ({doc.Id})");

        var wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"initial: {wf?.StatusName} | links: {string.Join(",", wf?.Links.Keys ?? [])}");

        await api.Workflow.PostWorkflowActionAsync(wf!.Links["submit"], new { reviewerId = me.UserId });
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"after submit: {wf?.StatusName} | assignedTo: {wf?.AssignedToName} | links: {string.Join(",", wf?.Links.Keys ?? [])}");

        var tasks = await api.Workflow.GetTasksAsync();
        Console.WriteLine($"tasks: {tasks.Count} -> {string.Join(",", tasks.Select(t => $"{t.DocumentName}/v{t.VersionNumber}"))}");

        await api.Workflow.PostWorkflowActionAsync(wf!.Links["approve"], null);
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"after approve: {wf?.StatusName} | links: {string.Join(",", wf?.Links.Keys ?? [])}");

        await api.Workflow.PostWorkflowActionAsync(wf!.Links["release"], null);
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"after release: {wf?.StatusName}");
        Console.WriteLine("history:");
        foreach (var h in wf!.History)
        {
            Console.WriteLine($"  {h.ToStatusName} by {h.PerformedByName}{(h.AssignedToName is { } a ? $" -> {a}" : "")}{(h.RejectionReason is { } r ? $" · {r}" : "")}");
        }
    }

    private static async Task RunSelfTestAsync(string accessToken)
    {
        var api = new Services.SimplArchiveApiClient(accessToken);

        var repositories = await api.Documents.GetRepositoriesAsync();
        Console.WriteLine($"repositories: {repositories.Count}");
        foreach (var repository in repositories)
        {
            Console.WriteLine($"  📁 {repository.Name} (hasChildren={repository.HasChildren})");
        }

        var root = repositories.FirstOrDefault();
        if (root is null)
        {
            Console.WriteLine("no repositories visible; stopping.");
            return;
        }

        var children = await api.Documents.GetChildrenAsync(root.Href("children"));
        Console.WriteLine($"children of '{root.Name}': {children.Count}");

        var document = children.FirstOrDefault(c => c.HasVersions);
        if (document is null)
        {
            Console.WriteLine("no document with a version in the first repository; stopping.");
            return;
        }

        var mask = await api.Documents.GetMaskAsync(document.Href("mask"));
        Console.WriteLine($"mask: {mask.Name ?? "(none)"} v{mask.VersionNumber}");

        var indexData = await api.Documents.GetIndexDataAsync(document.Href("index-data"));
        Console.WriteLine($"index-data fields: {indexData.Count}");
        foreach (var field in indexData)
        {
            Console.WriteLine($"  {field.FieldName} = {string.Join(", ", field.Values)}");
        }

        var comments = await api.Documents.GetCommentsAsync(document.Href("chat"));
        Console.WriteLine($"comments: {comments.Count}");

        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        Console.WriteLine($"preview: {(preview.PreviewUrl is null ? "(none)" : "resolved")} converted={preview.PreviewConverted}; download: {(preview.DownloadUrl is null ? "(none)" : "resolved")}");

        if (preview.PreviewUrl is not null)
        {
            var (bytes, contentType) = await Services.SimplArchiveApiClient.DownloadAsync(preview.PreviewUrl);
            Console.WriteLine($"preview content-type: {contentType} ({bytes.Length} bytes)");
        }

        if (preview.DownloadUrl is not null)
        {
            // Reconstruct the filename with the version's extension (Document.Name is a bare stem now).
            var fileName = document.Name.EndsWith(preview.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? document.Name
                : document.Name + preview.FileExtension;
            var path = await Services.NativeFileOpener.DownloadToTempAsync(preview.DownloadUrl, fileName);
            Console.WriteLine($"downloaded '{document.Name}' (ext '{preview.FileExtension}') -> {path} ({new FileInfo(path).Length} bytes)");
        }
    }
}
