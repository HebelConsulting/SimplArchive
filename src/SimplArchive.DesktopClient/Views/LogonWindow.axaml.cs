using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The startup logon window (ADR "Desktop logon window", login redesign slice B): username + tenant + language +
// Login. Ctrl/Cmd+P opens the tenant manager (and the tenant list refreshes when it closes).
public partial class LogonWindow : Window
{
    public LogonWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Once shown, let the VM run its network-backed self-update check (issue #271).
        Opened += (_, _) => (DataContext as ViewModels.LogonViewModel)?.Activate();
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            await new TenantManagerWindow().ShowDialog(this);
            // Refresh the tenant dropdown so it reflects any add/edit/remove (without replacing the VM, which
            // would drop the LoginSucceeded subscription the app relies on).
            (DataContext as ViewModels.LogonViewModel)?.RefreshTenants();
        }
    }
}
