using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The Users &amp; groups ribbon (#530, tranche 4). Every action opens a dialog that needs the window as its
/// parent (the RecycleBinRibbon shape), so the handlers stay on the window and this control only forwards;
/// refresh binds to the view-model directly and has no forwarder at all.
/// </summary>
public partial class PrincipalRibbon : UserControl
{
    public PrincipalRibbon() => AvaloniaXamlLoader.Load(this);

    private void OnNewUser(object? sender, RoutedEventArgs e) => Window()?.OnNewUser(sender, e);

    private void OnNewGroup(object? sender, RoutedEventArgs e) => Window()?.OnNewGroup(sender, e);

    private void OnCopy(object? sender, RoutedEventArgs e) => Window()?.OnCopyPrincipal(sender, e);

    private void OnDelete(object? sender, RoutedEventArgs e) => Window()?.OnDeletePrincipal(sender, e);

    private void OnManageServiceAccounts(object? sender, RoutedEventArgs e) => Window()?.OnManageServiceAccounts(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
