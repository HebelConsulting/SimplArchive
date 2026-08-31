using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Repositories ribbon (#519 tranche 5) — the last tab ribbon still living inline in MainWindow.axaml.
/// </summary>
/// <remarks>
/// Every handler forwards to the window, which is the IntrayRibbon/RecycleBinRibbon shape and is not
/// incidental: each of these opens a dialog, and a dialog needs a parent that only the window has.
/// </remarks>
public partial class RepositoriesRibbon : UserControl
{
    public RepositoriesRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnNewFolder(object? sender, RoutedEventArgs e) => Window()?.OnNewFolder(sender, e);

    private void OnRename(object? sender, RoutedEventArgs e) => Window()?.OnRename(sender, e);

    private void OnDelete(object? sender, RoutedEventArgs e) => Window()?.OnDelete(sender, e);

    private void OnVersions(object? sender, RoutedEventArgs e) => Window()?.OnVersions(sender, e);

    private void OnStartWorkflow(object? sender, RoutedEventArgs e) => Window()?.OnStartWorkflow(sender, e);

    private void OnExport(object? sender, RoutedEventArgs e) => Window()?.OnExport(sender, e);

    private void OnImport(object? sender, RoutedEventArgs e) => Window()?.OnImport(sender, e);

    private void OnSaveAs(object? sender, RoutedEventArgs e) => Window()?.OnSaveAs(sender, e);

    private void OnGoToLink(object? sender, RoutedEventArgs e) => Window()?.OnGoToLink(sender, e);

    private void OnWebDavRibbon(object? sender, RoutedEventArgs e) => Window()?.OnWebDavRibbon(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
