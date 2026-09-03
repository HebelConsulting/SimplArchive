using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The Activate/Renew module dialog (ADRs 0740/0743): thin code-behind on the BookingDialog pattern —
// construct with the view-model, load on open, close when the act succeeds or the user cancels.
public partial class ActivateModuleDialog : Window
{
    public ActivateModuleDialog()
    {
        InitializeComponent();
    }

    public ActivateModuleDialog(ActivateModuleDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Opened += async (_, _) => await viewModel.LoadAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
