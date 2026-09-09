using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// A module's per-tenant configuration (ADR 0772): thin code-behind on the ActivateModuleDialog pattern —
// construct with the view-model, load on open, close when the save succeeds or the user cancels.
public partial class ModuleSettingsDialog : Window
{
    public ModuleSettingsDialog()
    {
        InitializeComponent();
    }

    public ModuleSettingsDialog(ModuleSettingsDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Opened += async (_, _) => await viewModel.LoadAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
