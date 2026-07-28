using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// Filing dialog (ADR "S3-backed inbox", phase 2; "Context-aware inbox filing dialog"). ShowDialog<FilingResult?>
// returns the chosen target (a folder to file into, or a document to version), or null if cancelled / nothing
// selected.
public partial class FolderPickerDialog : Window
{
    public FolderPickerDialog()
    {
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Close(DataContext is FolderPickerViewModel vm ? vm.BuildResult() : null);
    }
}
