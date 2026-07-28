using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The Compare-versions modal (ADR "Document version comparison") — two version pickers + an inline diff, plus an
// optional Beyond Compare launch. Its VM is set as the DataContext by the caller after SetupAsync loads versions.
public partial class CompareVersionsDialog : Window
{
    public CompareVersionsDialog()
    {
        InitializeComponent();
    }

    public CompareVersionsDialog(CompareVersionsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
