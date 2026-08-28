using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The principal detail's code-behind is FORWARDING only (the TenantSettingsPane pattern): the real handlers
// stay on MainWindow, because they open dialogs that need the Window as owner and call VM methods the window
// already fronts. A second copy here would be the drift the one-implementation rule exists to prevent.
public partial class PrincipalDetailPane : UserControl
{
    public PrincipalDetailPane() => InitializeComponent();

    private void OnChangePrincipalPhoto(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnChangePrincipalPhoto(sender, e);

    private void OnRemovePrincipalPhoto(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnRemovePrincipalPhoto(sender, e);

    private void OnResetPrincipalPassword(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnResetPrincipalPassword(sender, e);

    private void OnResetPrincipalMfa(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnResetPrincipalMfa(sender, e);

    private void OnImpersonatePrincipal(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnImpersonatePrincipal(sender, e);
}
