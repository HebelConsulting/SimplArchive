using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// The choice offered when an item is dropped onto a folder — see ADR "Desktop drag-and-drop move and
// reference".
public enum DropAction
{
    Move,
    Reference,
}

// Modal asking whether a dropped item should be moved or referenced. ShowDialog<DropAction?> returns the
// choice, or null if cancelled.
public partial class DropActionDialog : Window
{
    public DropActionDialog() : this("")
    {
    }

    public DropActionDialog(string message)
    {
        InitializeComponent();
        MessageBlock.Text = message;
    }

    private void OnMove(object? sender, RoutedEventArgs e) => Close(DropAction.Move);

    private void OnReference(object? sender, RoutedEventArgs e) => Close(DropAction.Reference);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
