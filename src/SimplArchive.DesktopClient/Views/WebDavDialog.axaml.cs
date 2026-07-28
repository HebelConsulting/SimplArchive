using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.Views;

// Manage the app-specific WebDAV password (ADR "WebDAV gateway") — show the mount URL, generate/regenerate the
// password (shown once), or revoke. ShowDialog returns nothing; the account menu just opens it.
public partial class WebDavDialog : Window
{
    private readonly SimplArchiveApiClient _api;
    private bool _enabled;

    // Parameterless ctor for the XAML designer/loader only.
    public WebDavDialog() : this(null!)
    {
    }

    public WebDavDialog(SimplArchiveApiClient api)
    {
        InitializeComponent();
        _api = api;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var status = await _api.GetWebDavStatusAsync();
            UrlBox.Text = status.Url;
            UsernameBox.Text = status.Username;
            Apply(status.Enabled);
        }
        catch
        {
            StatusText.Text = "Could not load WebDAV status.";
        }
    }

    private void Apply(bool enabled)
    {
        _enabled = enabled;
        RevokeButton.IsVisible = enabled;
        GenerateButton.Content = enabled ? "Regenerate password" : "Generate password";
        StatusText.Text = enabled
            ? "WebDAV access is enabled. Regenerate to get a new password, or revoke to disable it."
            : "WebDAV access is not set up yet.";
    }

    private void OnGenerate(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        try
        {
            var status = await _api.GenerateWebDavPasswordAsync();
            UrlBox.Text = status.Url;
            UsernameBox.Text = status.Username;
            PasswordBox.Text = status.Password ?? "";
            PasswordPanel.IsVisible = true;
            Apply(true);
        }
        catch
        {
            StatusText.Text = "Could not generate the WebDAV password.";
        }
    });

    private void OnRevoke(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        try
        {
            await _api.RevokeWebDavPasswordAsync();
            PasswordPanel.IsVisible = false;
            Apply(false);
        }
        catch
        {
            StatusText.Text = "Could not revoke WebDAV access.";
        }
    });

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
