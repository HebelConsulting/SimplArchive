using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Shows an admin-reset generated password once (ADR "User password management") with a Copy button.
public partial class GeneratedPasswordDialog : Window
{
    public GeneratedPasswordDialog() : this("the user", "")
    {
    }

    public GeneratedPasswordDialog(string userName, string password)
    {
        InitializeComponent();
        Intro.Text = $"New password for {userName}. Copy it now — it won't be shown again. Ask them to change it after signing in.";
        PasswordBox.Text = password;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(PasswordBox.Text ?? "");
        }
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
