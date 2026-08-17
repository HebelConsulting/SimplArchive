using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

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
            var status = await _api.Profile.GetWebDavStatusAsync();
            UrlBox.Text = status.Url;
            UsernameBox.Text = status.Username;
            Apply(status.Enabled);
        }
        catch
        {
            StatusText.Text = Strings.Get("WdLoadError");
        }
    }

    private void Apply(bool enabled)
    {
        _enabled = enabled;
        RevokeButton.IsVisible = enabled;
        // Straight to a mounted drive from here, rather than closing the dialog and hunting for the ribbon (#461).
        OpenFolderButton.IsVisible = enabled;
        GenerateButton.Content = Strings.Get(enabled ? "WdRegenerate" : "WdGenerate");
        StatusText.Text = Strings.Get(enabled ? "WdStatusEnabled" : "WdStatusNotSetUp");
    }

    private void OnGenerate(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        try
        {
            var status = await _api.Profile.GenerateWebDavPasswordAsync();
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
            await _api.Profile.RevokeWebDavPasswordAsync();
            PasswordPanel.IsVisible = false;
            Apply(false);
        }
        catch
        {
            StatusText.Text = "Could not revoke WebDAV access.";
        }
    });

    private void OnCopyUrl(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync(UrlBox.Text));

    private void OnCopyUsername(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync(UsernameBox.Text));

    private void OnCopyPassword(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync(PasswordBox.Text));

    // One implementation for all three buttons, and it CONFIRMS: a copy that silently did nothing is
    // indistinguishable from one that worked until the user pastes.
    private async Task CopyAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(value);
        StatusText.Text = Strings.Get("WdCopied");
    }

    // "I have credentials" → a mounted drive, without leaving the dialog (#461). Mount failures are reported
    // here rather than thrown: the OS refuses for reasons this code cannot enumerate, and the user needs the
    // reason plus the URL they already have on screen.
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (OsFileManager.MountedPath() is { } mounted)
        {
            await OsFileManager.OpenWebDavAsync(mounted);
            return;
        }

        StatusText.Text = Strings.Get("MwWebDavMounting");
        var result = await OsFileManager.OpenWebDavAsync((UrlBox.Text ?? "").TrimEnd('/'));
        StatusText.Text = result.Success
            ? Strings.Get("MwWebDavMounted")
            : string.Format(Strings.Get("MwWebDavMountFailed"), result.Error);
    });

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
