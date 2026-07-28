using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// Modal listing the folders that reference an item (with full paths). Clicking a row's Open closes the
// dialog returning that folder's id; the main window then navigates to it. See ADR "References-of-an-item
// list".
public partial class ReferencesDialog : Window
{
    public ReferencesDialog()
    {
        InitializeComponent();
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReferencingFolderViewModel folder })
        {
            Close(folder.Id);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
