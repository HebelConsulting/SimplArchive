using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// Manage the app-specific IMAP password + view toggle (ADR "IMAP endpoint (read-only, first slice)", #562) —
// the WebDAV dialog's shape: connection values with per-value Copy, generate/regenerate (password shown once),
// revoke, plus the emails-only/everything switch.
public partial class ImapDialog : Window
{
    private readonly SimplArchiveApiClient _api;
    private ProfileClient.ImapAccessInfo? _status;
    private bool _loading;

    // Parameterless ctor for the XAML designer/loader only.
    public ImapDialog() : this(null!)
    {
    }

    public ImapDialog(SimplArchiveApiClient api)
    {
        InitializeComponent();
        _api = api;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            Apply(await _api.Profile.GetImapAccessAsync());
        }
        catch
        {
            StatusText.Text = Strings.Get("WdLoadError");
        }
    }

    private void Apply(ProfileClient.ImapAccessInfo status)
    {
        _loading = true;
        _status = status;
        var port = status.TlsPort ?? status.Port;
        ServerBox.Text = port is { } p ? $"{status.Host}:{p}" : status.Host;
        UsernameBox.Text = status.Username;
        ShowAllBox.IsChecked = status.ShowAllDocuments;
        RevokeButton.IsVisible = status.Enabled;
        GenerateButton.Content = Strings.Get(status.Enabled ? "WdRegenerate" : "WdGenerate");
        StatusText.Text = Strings.Get(status.Available
            ? status.Enabled ? "WdStatusEnabled" : "WdStatusNotSetUp"
            : "ImUnavailable");
        GenerateButton.IsEnabled = status.Available;
        ShowAllBox.IsEnabled = status.Available;
        _loading = false;
    }

    private void OnGenerate(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_status is not { } status)
        {
            return;
        }

        try
        {
            var generated = await _api.Profile.GenerateImapPasswordAsync(status);
            Apply(generated);
            PasswordBox.Text = generated.Password ?? "";
            PasswordPanel.IsVisible = true;
        }
        catch
        {
            StatusText.Text = Strings.Get("ImGenerateError");
        }
    });

    private void OnRevoke(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_status is not { } status)
        {
            return;
        }

        try
        {
            await _api.Profile.RevokeImapPasswordAsync(status);
            PasswordPanel.IsVisible = false;
            await LoadAsync();
        }
        catch
        {
            StatusText.Text = Strings.Get("ImRevokeError");
        }
    });

    private void OnShowAllChanged(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        // The loading guard keeps the initial Apply() from writing the value it just read back to the server.
        if (_loading || _status is not { } status)
        {
            return;
        }

        // The box has ALREADY moved by the time this runs, so a failure that only writes a status line leaves it
        // showing a setting the server never stored — ADR 0724's "lying state", the same defect as the web
        // switch and the Intray toggles. Unlike those it was never silent (a missing `settings` rel throws into
        // the catch below), which is precisely why it was easy to miss: an error message is not the same as an
        // accurate control, and this one silently reverts on the next open.
        var previous = ShowAllBox.IsChecked != true;

        try
        {
            await _api.Profile.SetImapShowAllDocumentsAsync(status, ShowAllBox.IsChecked == true);
            StatusText.Text = Strings.Get("ImSettingsSaved");
        }
        catch
        {
            StatusText.Text = Strings.Get("ImSettingsError");

            // Put it back, behind the same re-entrancy guard Apply() uses — assigning IsChecked re-raises this
            // handler, and without the guard the revert would try to write the old value back to the server.
            _loading = true;
            ShowAllBox.IsChecked = previous;
            _loading = false;
        }
    });

    private void OnCopyServer(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync(ServerBox.Text));

    private void OnCopyUsername(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync(UsernameBox.Text));

    private void OnCopyPassword(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync(PasswordBox.Text));

    private async Task CopyAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(value);
        StatusText.Text = Strings.Get("WdCopied");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
