using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Self-service password change (ADR "User password management"). ShowDialog<ChangePasswordDialog.Result?>
// returns the current + new password, or null if cancelled/invalid. The caller (VM) does the API call.
public partial class ChangePasswordDialog : Window
{
    public ChangePasswordDialog()
    {
        InitializeComponent();
        Opened += (_, _) => CurrentBox.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var current = CurrentBox.Text ?? "";
        var @new = NewBox.Text ?? "";
        var confirm = ConfirmBox.Text ?? "";

        if (current.Length == 0 || @new.Length == 0)
        {
            return;
        }

        if (@new != confirm)
        {
            Error.IsVisible = true;
            return;
        }

        Close(new Result(current, @new));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    public sealed record Result(string Current, string New);
}
