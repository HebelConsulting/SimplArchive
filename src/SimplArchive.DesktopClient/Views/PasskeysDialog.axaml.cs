using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.Views;

// Passkey management (ADR "Desktop passkey management"). List + remove are plain API calls made natively;
// adding a passkey needs a WebAuthn attestation ceremony, which a native window can't run, so "Add a passkey"
// opens the server-rendered page in the system browser (via OidcLoopbackAuthenticator) and the list refreshes
// when the browser hands back. ShowDialog returns nothing — the account menu just opens it.
public partial class PasskeysDialog : Window
{
    private readonly SimplArchiveApiClient _api;
    private readonly ObservableCollection<PasskeyRow> _rows = [];

    // Parameterless ctor for the XAML designer/loader only.
    public PasskeysDialog() : this(null!)
    {
    }

    public PasskeysDialog(SimplArchiveApiClient api)
    {
        InitializeComponent();
        _api = api;
        KeyList.ItemsSource = _rows;
        Opened += async (_, _) => await LoadAsync();
    }

    // Carries the row's own removal address (its `self` rel) rather than just an id, so the delete button
    // follows what the server advertised (ADR 0543, issue #416).
    public sealed record PasskeyRow(Guid Id, string Name, string Meta, string? RemoveHref);

    private async Task LoadAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var keys = await _api.Profile.GetPasskeysAsync();
            _rows.Clear();
            foreach (var k in keys)
            {
                var meta = $"Added {k.CreatedAt.LocalDateTime:yyyy-MM-dd}"
                    + (k.LastUsedAt is { } u ? $" · last used {u.LocalDateTime:yyyy-MM-dd}" : "");
                _rows.Add(new PasskeyRow(k.Id, k.Name, meta, k.RemoveHref));
            }

            EmptyLabel.IsVisible = _rows.Count == 0;
            KeyList.IsVisible = _rows.Count > 0;
            Error.IsVisible = false;
        }
        catch (Exception)
        {
            Error.Text = "Could not load your passkeys.";
            Error.IsVisible = true;
        }
    }

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        AddButton.IsEnabled = false;
        try
        {
            // Delegates the attestation ceremony to the system browser; returns true if a passkey was added.
            await new OidcLoopbackAuthenticator().ManagePasskeysAsync();
            await LoadAsync();
        }
        catch (Exception)
        {
            Error.Text = "Could not add a passkey.";
            Error.IsVisible = true;
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PasskeyRow row })
        {
            return;
        }

        if (!await new ConfirmDialog($"Remove the passkey '{row.Name}'? You won't be able to sign in with it anymore.", "Remove").ShowDialog<bool>(this))
        {
            return;
        }

        try
        {
            if (row.RemoveHref is not { } removeHref) { return; }

            await _api.Profile.RemovePasskeyAsync(removeHref);
            await LoadAsync();
        }
        catch (Exception)
        {
            Error.Text = "Could not remove the passkey.";
            Error.IsVisible = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
