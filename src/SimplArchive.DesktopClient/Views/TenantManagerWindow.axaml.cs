using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The Ctrl/Cmd+P tenant manager (ADR "Desktop tenant configuration") — a tenants list + a read-only/editable
// data pane for the deployment's name + API-root URL.
public partial class TenantManagerWindow : Window
{
    public TenantManagerWindow()
    {
        InitializeComponent();
        DataContext = new TenantManagerViewModel();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
