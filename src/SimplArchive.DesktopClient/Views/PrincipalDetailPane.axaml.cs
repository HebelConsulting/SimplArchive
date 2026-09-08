using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The principal detail's code-behind is FORWARDING only (the TenantSettingsPane pattern): the real handlers
// stay on MainWindow, because they open dialogs that need the Window as owner and call VM methods the window
// already fronts. A second copy here would be the drift the one-implementation rule exists to prevent.
public partial class PrincipalDetailPane : UserControl
{
    public PrincipalDetailPane() => InitializeComponent();

    // Esc cancels, ⌘/Ctrl+S saves (ADR 0550's keyboard half — the desktop may take ⌘S, the web may not).
    private void OnEmailKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Avalonia.Input.Key.Escape)
        {
            vm.CancelPrincipalEmailEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Avalonia.Input.Key.Enter
            || (e.Key == Avalonia.Input.Key.S
                && (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)
                    || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))))
        {
            vm.SavePrincipalEmailCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnChangePrincipalPhoto(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnChangePrincipalPhoto(sender, e);

    private void OnRemovePrincipalPhoto(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnRemovePrincipalPhoto(sender, e);

    private void OnResetPrincipalPassword(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnResetPrincipalPassword(sender, e);

    private void OnResetPrincipalMfa(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnResetPrincipalMfa(sender, e);

    private void OnImpersonatePrincipal(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnImpersonatePrincipal(sender, e);
}
