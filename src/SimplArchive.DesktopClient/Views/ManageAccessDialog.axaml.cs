using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The desktop Manage-access dialog (ADR "Manage-access UI for document/folder ACLs"). Self-contained: the VM
// loads the grants/picker and does the PUT/DELETE. A nested ConfirmDialog (owned by this window) backs the
// remove confirmation.
public partial class ManageAccessDialog : Window
{
    public ManageAccessDialog() : this(new ManageAccessViewModel())
    {
    }

    public ManageAccessDialog(ManageAccessViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ConfirmAsync = (message, confirmLabel) => new ConfirmDialog(message, confirmLabel).ShowDialog<bool>(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
