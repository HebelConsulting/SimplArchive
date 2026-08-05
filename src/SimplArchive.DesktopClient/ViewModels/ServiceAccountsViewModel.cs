using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Service Accounts manager (ADR 0534) — the machine-to-machine credentials surface, opened
// from the Users & groups tab and gated on CanManageServiceAccounts. Mirrors the web ServiceAccountsDialog:
// a create form (name + rights) on top, then the list with per-row edit / rotate-secret / revoke. The window
// code-behind orchestrates the sub-dialogs (edit / one-time-secret / confirm) — this VM holds the state and the
// API calls, so a DesktopUiEndToEndTests case can drive it (and the client) without XAML.
public sealed partial class ServiceAccountsViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _client;

    public ServiceAccountsViewModel(SimplArchiveApiClient client) => _client = client;

    public SimplArchiveApiClient Client => _client;

    public ObservableCollection<ServiceAccountRowViewModel> Accounts { get; } = [];

    // The create form. Only the five grantable rights (the server caps them at the caller's own).
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private bool _newCanExport;
    [ObservableProperty] private bool _newCanImport;
    [ObservableProperty] private bool _newCanManageRepositories;
    [ObservableProperty] private bool _newCanManageMasks;
    [ObservableProperty] private bool _newCanManageServiceAccounts;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var accounts = await _client.GetServiceAccountsAsync(cancellationToken);
            Accounts.Clear();
            foreach (var a in accounts)
            {
                Accounts.Add(new ServiceAccountRowViewModel(a));
            }

            Status = "";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    // The create form's rights, folded into the shared SystemRightsData shape (only the five grantable fields set).
    public SimplArchiveApiClient.SystemRightsData NewRights() => new(
        false, false, false, false, false, false,
        NewCanManageRepositories, NewCanManageMasks, NewCanManageServiceAccounts, false, false,
        NewCanExport, NewCanImport);

    public void ResetNewForm()
    {
        NewName = "";
        NewCanExport = NewCanImport = NewCanManageRepositories = NewCanManageMasks = NewCanManageServiceAccounts = false;
    }
}

// One row in the list — wraps the API DTO and renders a short, localized rights summary for display.
public sealed class ServiceAccountRowViewModel
{
    public ServiceAccountRowViewModel(SimplArchiveApiClient.ServiceAccountInfo info) => Info = info;

    public SimplArchiveApiClient.ServiceAccountInfo Info { get; }

    public Guid Id => Info.Id;
    public string Name => Info.Name;
    public string ClientId => Info.ClientId;
    public bool IsActive => Info.IsActive;

    public string RightsSummary => string.Join(", ", RightLabels());

    private IEnumerable<string> RightLabels()
    {
        if (Info.CanExport) { yield return Strings.Get("SaRightExport"); }
        if (Info.CanImport) { yield return Strings.Get("SaRightImport"); }
        if (Info.CanManageRepositories) { yield return Strings.Get("SaRightRepositories"); }
        if (Info.CanManageMasks) { yield return Strings.Get("SaRightMasks"); }
        if (Info.CanManageServiceAccounts) { yield return Strings.Get("SaRightServiceAccounts"); }
    }
}
