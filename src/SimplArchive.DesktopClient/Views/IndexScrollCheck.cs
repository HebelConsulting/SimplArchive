using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The headless check behind <c>--indexscroll-test</c>: the detail pane's index-data area actually SCROLLS
/// when its content is taller than the room it has, and fills the pane when the bottom half is collapsed.
/// </summary>
/// <remarks>
/// <para>
/// A hook rather than a screenshot, because scrollability is not something a capture can show — the defect
/// looked exactly like a short document, and every capture we take was of a short one.
/// </para>
/// <para>
/// <b>The defect.</b> The cap that stops a long mask pushing the preview off the bottom used to live on the
/// <c>RowDefinition</c>. An <b>Auto</b> row measures its child with an INFINITE height, so the ScrollViewer
/// concluded viewport = extent and had nothing to scroll; the row's <c>MaxHeight</c> then applied at arrange
/// and merely CLIPPED. Measured here across mask lengths: with the cap on the row, every mask of two fields or
/// more reported <c>viewport == extent</c> and could not scroll. Moving the cap onto the ScrollViewer makes it
/// report <c>min(content, cap)</c> as its desired height, so the Auto row still fits short content and the
/// viewport is real. Put the <c>MaxHeight</c> back on the RowDefinition and this prints FAILED.
/// </para>
/// <para>
/// <b>Two harness traps, both of which produced a confident false PASS while this was being written.</b>
/// (1) The fields must be added to the view model BEFORE the pane is built: populating an already-shown pane
/// re-measures it with a constrained height and hides the very defect. (2) The check drives ToggleBottom,
/// which <c>SaveLayout()</c>s — without <c>PathOverride</c> it writes the DEVELOPER'S real layout file, so the
/// next run starts collapsed and measures a different pane than the one it reports on.
/// </para>
/// </remarks>
public static class IndexScrollCheck
{
    /// <summary>The mask lengths swept — 0 fields is the short case that must NOT gain a scrollbar.</summary>
    private static readonly int[] MaskLengths = [0, 2, 8, 40];

    /// <summary>Runs the check and prints <c>OK</c> or <c>FAILED</c>. Returns true when everything held.</summary>
    public static bool Run()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        // See the remarks: without this the check rewrites the developer's own saved layout.
        Services.LayoutSettingsStore.PathOverride =
            Path.Combine(Path.GetTempPath(), $"simplarchive-indexscroll-{Guid.NewGuid():N}.json");

        var ok = true;

        // The invariant, and it is deliberately NOT "a long mask scrolls": a mask that FITS must not grow a
        // scrollbar, and one that does not fit must be reachable. So the property is that nothing is ever cut
        // off without a way to get at it — which is exactly the bug as reported.
        var everOverflowed = false;

        Console.WriteLine("fields   viewport   extent   clipped   scrolls");
        foreach (var fields in MaskLengths)
        {
            var (scroller, _) = BuildPane(fields, height: 700);
            if (scroller is null)
            {
                Console.WriteLine("index ScrollViewer: NOT FOUND (the check cannot measure anything)");
                Console.WriteLine("FAILED");
                return false;
            }

            var scrolls = scroller.Extent.Height > scroller.Viewport.Height + 0.5;
            var clipped = scroller.Bounds.Height + 0.5 < scroller.Extent.Height;
            everOverflowed |= clipped;

            Console.WriteLine($"{fields,6}   {scroller.Viewport.Height,8:F0}   {scroller.Extent.Height,6:F0}"
                + $"   {clipped,7}   {scrolls}");
            if (clipped && !scrolls)
            {
                Console.WriteLine("  ^ CUT OFF with no way to reach it — this is the defect");
                ok = false;
            }
        }

        // Anti-vacuous: if no case was ever taller than its room, the loop above proved nothing at all.
        if (!everOverflowed)
        {
            Console.WriteLine("no mask overflowed its room — the check measured nothing");
            ok = false;
        }

        // Collapsing the bottom half lifts the cap, so the index data fills the pane rather than stopping at
        // 280 with grey space beneath it.
        var (collapsedScroller, vm) = BuildPane(fields: 40, height: 700);
        var capped = collapsedScroller!.Viewport.Height;
        vm.ToggleBottomCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var grew = collapsedScroller.Viewport.Height > capped + 0.5;
        Console.WriteLine($"collapsing the bottom half lets it fill the pane: {grew} "
            + $"({capped:F0} -> {collapsedScroller.Viewport.Height:F0})");
        ok &= grew;

        Console.WriteLine(ok ? "OK" : "FAILED");
        return ok;
    }

    /// <summary>
    /// A shown detail pane whose view model ALREADY carries <paramref name="fields"/> index fields — the order
    /// matters, see the remarks.
    /// </summary>
    private static (ScrollViewer? Scroller, MainWindowViewModel Vm) BuildPane(int fields, double height)
    {
        var vm = new MainWindowViewModel();
        vm.PopulateDemoForScreenshot();
        vm.IndexFields.Clear();
        for (var i = 0; i < fields; i++)
        {
            vm.IndexFields.Add(new IndexFieldViewModel { FieldName = $"Field {i}", Values = $"Value {i}" });
        }

        var pane = new DocumentDetailPane { DataContext = vm };
        new Window { Content = pane, Width = 900, Height = height }.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (FindIndexScroller(pane), vm);
    }

    /// <summary>The index-data ScrollViewer, found by the automation id the pane already carries.</summary>
    private static ScrollViewer? FindIndexScroller(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => AutomationProperties.GetAutomationId(s) == "tour:pane-index");
}
