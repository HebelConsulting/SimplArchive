using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Shows a service account's client_id + one-time client_secret after create/rotate (ADR 0534) — the secret is
// never retrievable again (OpenIddict stores it hashed), so the admin copies it now. The desktop analog of the
// web ServiceAccountSecretDialog / the GeneratedPasswordDialog copy-once pattern.
public partial class ServiceAccountSecretDialog : Window
{
    public ServiceAccountSecretDialog() : this("", "")
    {
    }

    public ServiceAccountSecretDialog(string clientId, string clientSecret)
    {
        InitializeComponent();
        ClientIdBox.Text = clientId;
        ClientSecretBox.Text = clientSecret;
    }

    private async void OnCopyClientId(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(ClientIdBox.Text ?? "");
        }
    }

    private async void OnCopyClientSecret(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(ClientSecretBox.Text ?? "");
        }
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
