using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// A small yes/no confirmation modal. ShowDialog<bool> returns true if confirmed. See ADR "Desktop
// delete/rename".
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("Are you sure?", "OK")
    {
    }

    public ConfirmDialog(string message, string confirmLabel = "OK")
    {
        InitializeComponent();
        MessageBlock.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
