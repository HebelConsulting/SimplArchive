using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The tenant sensitivity-label management dialog (ADR "Configurable sensitivity labels + upload defaults").
public partial class SensitivityLabelsDialog : Window
{
    public SensitivityLabelsDialog() => InitializeComponent();

    public SensitivityLabelsDialog(SensitivityLabelsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
