using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// User impersonation (ADR "User impersonation"): starting it from the admin's own session, the banner state
// while it lasts, and stopping it.
//
// _adminApi is the admin's own client, kept so Stop can revert -- which is also why impersonation is ONE
// level: it is started only from the admin's session, so there is exactly one client to go back to.
//
// Out of the same "Intray" heading as Checkout, which held six subjects and no intray (#941).
public sealed partial class MainWindowViewModel
{
    // User impersonation (ADR "User impersonation"): CanImpersonate gates the "Impersonate" action; while
    // IsImpersonating, a banner shows ImpersonatedName + a Stop button. _adminApi is the pre-impersonation client
    // kept so Stop can revert. Impersonation is one level (started only from the admin's own session).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanImpersonateSelectedPrincipal))] private bool _canImpersonate;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanImpersonateSelectedPrincipal))] private bool _isImpersonating;
    [ObservableProperty] private string? _impersonatedName;
    private SimplArchiveApiClient? _adminApi;

    // Impersonate a target user: exchange the admin token (RFC 8693), swap the api client, and reload the
    // workbench as that user. The server enforces the rules (non-admin, active, same tenant).
    public async Task ImpersonateAsync(Guid targetUserId)
    {
        if (_api is not { } api || IsImpersonating)
        {
            return;
        }

        var token = await SimplArchiveApiClient.ExchangeImpersonationTokenAsync(api.AccessToken, targetUserId);
        if (token is null)
        {
            ReportError(Strings.Get("StErrImpersonate"));
            return;
        }

        _adminApi = api;
        UseApi(new SimplArchiveApiClient(token));
        await SetupUserContextAsync();
        await LoadRootAsync();
    }

    [RelayCommand]
    private async Task StopImpersonating()
    {
        if (_adminApi is not { } admin)
        {
            return;
        }

        UseApi(admin);
        _adminApi = null;
        await SetupUserContextAsync();
        await LoadRootAsync();
    }
}
