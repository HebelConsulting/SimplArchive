using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The Ctrl/Cmd+P server manager (ADR "Desktop server configuration") — a server list + a read-only/editable
// data pane for the deployment's name + API-root URL.
public partial class ServerManagerWindow : Window
{
    public ServerManagerWindow()
    {
        InitializeComponent();
        var vm = new ServerManagerViewModel();
        DataContext = vm;
        // Once shown, enable the VM's network-backed "is this our server?" URL probe (issue #270).
        Opened += (_, _) => vm.Activate();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
