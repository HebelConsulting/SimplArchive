using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The contents-list column interactions (#786, ADR 0705): the header edge drags, and the measured pane width
/// the flexible Name column is computed from.
/// </summary>
/// <remarks>
/// A partial of its own because <c>MainWindow.axaml.cs</c> is on the 1000-line standing-debt list (issue #466)
/// and may only get smaller.
/// </remarks>
public partial class MainWindow
{
    // Dragging a contents-list header column's right-edge Thumb resizes that column (ADR "Desktop list-pane
    // resizable columns"); the Thumb's Tag carries the 0-based column index. Persisted on drag completion.
    private void OnColumnResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control { Tag: { } tag }
            && int.TryParse(tag.ToString(), out var index))
        {
            vm.ResizeColumn(index, e.Vector.X);
        }
    }

    private void OnColumnResizeDone(object? sender, VectorEventArgs e) => (DataContext as MainWindowViewModel)?.SaveLayout();

    // The list pane's measured viewport width, which the Name column is computed from so the table fills the
    // pane exactly (#786). Pushed from the view because it is a LAYOUT fact: the view-model holds the pane
    // split as star units and cannot know what those resolve to in pixels.
    private void OnContentsPaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is ScrollViewer viewer)
        {
            vm.ContentsPaneWidth = viewer.Viewport.Width > 0 ? viewer.Viewport.Width : viewer.Bounds.Width;
        }
    }
}
