using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Small modal dialog to rename an item, pre-filled with the current name. ShowDialog<string?> returns the
// trimmed new name, or null if cancelled/empty. See ADR "Desktop delete/rename".
public partial class RenameDialog : Window
{
    public RenameDialog() : this("")
    {
    }

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Opened += (_, _) =>
        {
            NameBox.SelectAll();
            NameBox.Focus();
        };
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
            }
        };
    }

    private void OnRename(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        var name = NameBox.Text?.Trim();
        Close(string.IsNullOrEmpty(name) ? null : name);
    }
}
