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

    /// <summary>
    /// The same dialog for a value a module action reveals ONCE (ABI 1.8, core ADR 0899): an issued credential, a
    /// push key. The module's label is the title; the sentence is the generic "copy it now".
    /// </summary>
    public static GeneratedPasswordDialog ForRevealedValue(string label, string value)
    {
        var dialog = new GeneratedPasswordDialog { Title = label };
        dialog.Intro.Text = SimplArchive.Localization.Strings.Get("RevealOnceHint");
        dialog.PasswordBox.Text = value;
        return dialog;
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
