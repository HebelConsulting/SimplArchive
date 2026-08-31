using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Repositories tab's contents pane (#519 tranche 7).</summary>
/// <remarks>
/// The column and scroll-sync handlers live HERE rather than forwarding, unlike the dialog-opening ones: they
/// are about this pane's own layout and reach its own named controls. That is also what fixes them — the sync
/// looked the header scroller up in the WINDOW's namescope, which no longer contains it.
/// </remarks>
public partial class ContentsListPane : UserControl
{
    // InitializeComponent(), not AvaloniaXamlLoader.Load(this): the latter leaves the generated x:Name fields
    // null, and both ContentsList and OpenMenuItem are read below (#519 tranche 6 learned this the hard way).
    public ContentsListPane()
    {
        InitializeComponent();

        // Advertise the Open chord in the menu entry itself — a shortcut nobody can discover is one nobody
        // uses. Set here rather than in XAML because it is ⌘ on macOS and Ctrl elsewhere; moved from the
        // window's constructor with the menu item it describes.
        OpenMenuItem.InputGesture = Shortcuts.Open;
    }

    /// <summary>The row list, for the window's cross-pane drag-and-drop wiring.</summary>
    internal ListBox List => ContentsList;

    // Dragging a contents-list header column's right-edge Thumb resizes that column (ADR "Desktop list-pane
    // resizable columns"); the Thumb's Tag carries the 0-based column index. Persisted on drag completion.
    internal void OnColumnResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control { Tag: { } tag }
            && int.TryParse(tag.ToString(), out var index))
        {
            vm.ResizeColumn(index, e.Vector.X);
        }
    }

    internal void OnColumnResizeDone(object? sender, VectorEventArgs e) => (DataContext as MainWindowViewModel)?.SaveLayout();

    // The list pane's measured viewport width, which the Name column is computed from so the table fills the
    // pane exactly (#786). Pushed from the view because it is a LAYOUT fact: the view-model holds the pane
    // split as star units and cannot know what those resolve to in pixels.
    internal void OnContentsPaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is ScrollViewer viewer)
        {
            vm.ContentsPaneWidth = viewer.Viewport.Width > 0 ? viewer.Viewport.Width : viewer.Bounds.Width;
        }
    }

    // The header strip follows the body's horizontal offset, so the column captions stay over their columns
    // while the body alone carries the scrollbars (the vertical one at the pane's edge — see the axaml note).
    internal void OnContentsBodyScroll(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer body && this.FindControl<ScrollViewer>("ContentsHeaderScroller") is { } header)
        {
            header.Offset = new Avalonia.Vector(body.Offset.X, 0);
        }
    }

    private void OnBulkAddTags(object? sender, RoutedEventArgs e) => Window()?.OnBulkAddTags(sender, e);

    private void OnBulkDelete(object? sender, RoutedEventArgs e) => Window()?.OnBulkDelete(sender, e);

    private void OnBulkMove(object? sender, RoutedEventArgs e) => Window()?.OnBulkMove(sender, e);

    private void OnBulkSensitivity(object? sender, RoutedEventArgs e) => Window()?.OnBulkSensitivity(sender, e);

    private void OnCompareVersions(object? sender, RoutedEventArgs e) => Window()?.OnCompareVersions(sender, e);

    private void OnCopyDeepLink(object? sender, RoutedEventArgs e) => Window()?.OnCopyDeepLink(sender, e);

    private void OnDelete(object? sender, RoutedEventArgs e) => Window()?.OnDelete(sender, e);

    private void OnGoTo(object? sender, RoutedEventArgs e) => Window()?.OnGoTo(sender, e);

    private void OnManageAccess(object? sender, RoutedEventArgs e) => Window()?.OnManageAccess(sender, e);

    private void OnOpen(object? sender, RoutedEventArgs e) => Window()?.OnOpen(sender, e);

    private void OnPlaceLegalHold(object? sender, RoutedEventArgs e) => Window()?.OnPlaceLegalHold(sender, e);

    private void OnReferences(object? sender, RoutedEventArgs e) => Window()?.OnReferences(sender, e);

    private void OnRename(object? sender, RoutedEventArgs e) => Window()?.OnRename(sender, e);

    private void OnSaveAs(object? sender, RoutedEventArgs e) => Window()?.OnSaveAs(sender, e);

    private void OnStartWorkflow(object? sender, RoutedEventArgs e) => Window()?.OnStartWorkflow(sender, e);

    private void OnVersions(object? sender, RoutedEventArgs e) => Window()?.OnVersions(sender, e);

    private void OnContentsContextRequested(object? sender, ContextRequestedEventArgs e) => Window()?.OnContentsContextRequested(sender, e);

    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => Window()?.OnListDoubleTapped(sender, e);

    private void OnListKeyDown(object? sender, KeyEventArgs e) => Window()?.OnListKeyDown(sender, e);

    private void OnContentsBackgroundPressed(object? sender, PointerPressedEventArgs e) => Window()?.OnContentsBackgroundPressed(sender, e);

    private void OnContentsSelectionChanged(object? sender, SelectionChangedEventArgs e) => Window()?.OnContentsSelectionChanged(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
