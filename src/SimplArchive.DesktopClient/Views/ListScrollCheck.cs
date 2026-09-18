using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Headless check that the contents list scrolls vertically and the filter row narrows it, for
/// <c>--list-scroll-test</c> (#48).
/// </summary>
/// <remarks>
/// A RENDERED check, not a view-model one: "there is a scrollbar" is a statement about the visual tree. The
/// pane's outer ScrollViewer deliberately disables vertical scrolling — it exists to co-scroll the header and
/// rows horizontally — so the vertical job belongs to the ListBox's own ScrollViewer, and only a layout pass
/// can say whether it does it.
/// </remarks>
internal static class ListScrollCheck
{
    internal static void Run()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        var vm = new MainWindowViewModel();
        vm.IsLoggedIn = true; // the workbench is IsVisible-gated on login (see ColumnDragCheck)
        var window = new Views.MainWindow { DataContext = vm, Width = 1100, Height = 600 };
        for (var i = 0; i < 200; i++)
        {
            vm.Items.Add(new NodeViewModel
            {
                Id = Guid.NewGuid(),
                Name = $"Row {i:000}",
                HasChildren = false,
                HasVersions = true,
                DocumentType = i % 2 == 0 ? "Basic Entry" : "eMail",
                CreatedBy = i % 3 == 0 ? "Anna" : "Tom",
            });
        }
        window.Show();
        Dispatcher.UIThread.RunJobs();
        using (var _ = window.CaptureRenderedFrame()) { } // compose, so scroll extents are real
        Dispatcher.UIThread.RunJobs();

        var list = window.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>()
            .First(l => l.Name == "ContentsList");
        var scroller = window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>()
            .First(v => v.Name == "ContentsBodyScroller");
        var scrollable = scroller.Extent.Height > scroller.Viewport.Height + 1;

        scroller.Offset = new Avalonia.Vector(0, 500);
        Dispatcher.UIThread.RunJobs();
        var moved = scroller.Offset.Y > 0;

        // The point of the two-scroller shape: the vertical scrollbar must sit INSIDE the visible pane,
        // not at the right edge of the horizontally-scrolled column block where no ordinary pane width
        // ever shows it (the defect this harness exists for).
        using (var _ = window.CaptureRenderedFrame()) { } // re-compose after the offset change
        Dispatcher.UIThread.RunJobs();
        var vbar = scroller.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical
                && ReferenceEquals(b.TemplatedParent, scroller)); // NOT the ListBox's disabled internal one
        var pt = vbar.TranslatePoint(new Avalonia.Point(0, 0), scroller);
        Console.WriteLine($"DIAG vbar visible={vbar.IsVisible}/{vbar.IsEffectivelyVisible} bounds={vbar.Bounds} pt={pt} paneViewport={scroller.Viewport.Width:F0}");
        var barVisible = vbar.IsEffectivelyVisible
            && pt is { } q
            && q.X < scroller.Viewport.Width + 20;

        // The filter narrows what the list shows, and clearing it restores everything.
        vm.ContentsFilterName = "Row 00";
        Dispatcher.UIThread.RunJobs();
        var filtered = vm.VisibleItems.Count == 10 && list.ItemCount == 10;

        // And the hint is ON SCREEN while it is narrowed (#1275). Asserted through IsEffectivelyVisible on a
        // composed frame rather than on the view-model flag: the property was DECLARED for this and bound in
        // no view for months, so "the flag is true" is exactly the evidence that proved worthless. A binding
        // that resolves to nothing, or a control placed where it cannot be seen, passes every other check here.
        using (var _ = window.CaptureRenderedFrame()) { }
        Dispatcher.UIThread.RunJobs();
        var hint = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => (string?)t.GetValue(AutomationProperties.AutomationIdProperty) == "contents-filter-hint");
        var hintShown = hint is { IsEffectivelyVisible: true } h
            && h.Bounds.Width > 0 && h.Bounds.Height > 0
            && !string.IsNullOrWhiteSpace(h.Text);
        Console.WriteLine($"DIAG hint shown={hintShown} text='{hint?.Text}' bounds={hint?.Bounds}");

        vm.ContentsFilterName = string.Empty;
        Dispatcher.UIThread.RunJobs();
        using (var _ = window.CaptureRenderedFrame()) { }
        Dispatcher.UIThread.RunJobs();
        var restored = vm.VisibleItems.Count == 200;
        // ...and gone again once nothing is filtered, so it is not permanent furniture in the pane.
        var hintHidden = hint is null || !hint.IsEffectivelyVisible;

        Console.WriteLine($"scrollable={scrollable} moved={moved} barVisible={barVisible} filtered={filtered} restored={restored} hintShown={hintShown} hintHidden={hintHidden} extent={scroller.Extent.Height:F0} viewport={scroller.Viewport.Height:F0}");
        Console.WriteLine(scrollable && moved && barVisible && filtered && restored && hintShown && hintHidden ? "OK" : "FAILED");
    }
}
