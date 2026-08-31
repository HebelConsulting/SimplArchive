using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Repositories tab's tree pane (#519 tranche 6).</summary>
public partial class FolderTreePane : UserControl
{
    // InitializeComponent(), NOT AvaloniaXamlLoader.Load(this): the latter loads the XAML but leaves the
    // generated x:Name fields NULL, so FolderTree below would be null and the window's drag-drop wiring would
    // fail with a bare NullReferenceException at construction. The other extracted controls get away with
    // Load(this) only because none of them reads a named field.
    public FolderTreePane() => InitializeComponent();

    /// <summary>The TreeView itself, for the window's CROSS-PANE wiring.</summary>
    /// <remarks>
    /// Exposed rather than hidden because <c>WorkbenchDragDrop</c> coordinates the tree, the contents list and
    /// the intray list together, and the tapped-gesture handler is added at the window. Neither belongs to a
    /// single pane, so neither could move in with the markup.
    /// </remarks>
    internal TreeView Tree => FolderTree;

    // ContextRequested, not Click: it decides which context-menu entries the right-clicked node offers.
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e) => Window()?.OnTreeContextRequested(sender, e);

    private void OnTreeRefresh(object? sender, RoutedEventArgs e) => Window()?.OnTreeRefresh(sender, e);

    private void OnTreeUpload(object? sender, RoutedEventArgs e) => Window()?.OnTreeUpload(sender, e);

    private void OnTreeRename(object? sender, RoutedEventArgs e) => Window()?.OnTreeRename(sender, e);

    private void OnTreeDelete(object? sender, RoutedEventArgs e) => Window()?.OnTreeDelete(sender, e);

    private void OnTreeMove(object? sender, RoutedEventArgs e) => Window()?.OnTreeMove(sender, e);

    private void OnTreeManageAccess(object? sender, RoutedEventArgs e) => Window()?.OnTreeManageAccess(sender, e);

    private void OnTreeReferences(object? sender, RoutedEventArgs e) => Window()?.OnTreeReferences(sender, e);

    private void OnTreePlaceReference(object? sender, RoutedEventArgs e) => Window()?.OnTreePlaceReference(sender, e);

    private void OnTreePlaceLegalHold(object? sender, RoutedEventArgs e) => Window()?.OnTreePlaceLegalHold(sender, e);

    private void OnTreeTakeOver(object? sender, RoutedEventArgs e) => Window()?.OnTreeTakeOver(sender, e);

    private void OnTreeToggleFollow(object? sender, RoutedEventArgs e) => Window()?.OnTreeToggleFollow(sender, e);

    private void OnTreeFolderSort(object? sender, RoutedEventArgs e) => Window()?.OnTreeFolderSort(sender, e);

    private void OnExport(object? sender, RoutedEventArgs e) => Window()?.OnExport(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
