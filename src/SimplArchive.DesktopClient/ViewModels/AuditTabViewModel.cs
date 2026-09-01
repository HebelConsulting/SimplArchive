using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// Backs the desktop Audit tab (ADR "Desktop audit viewer"): the filtered event table, the chain/WORM verify
/// pair, retention (read-only with a pencil, ADR 0550), purge and export.
/// </summary>
/// <remarks>
/// Extracted from <c>MainWindowViewModel</c> (#517, tranche 1) — the CheckoutTabViewModel shape: its own
/// view-model because a tab's worth of state belongs to the tab, wired to the shell through
/// <see cref="IShellContext"/> and <see cref="SetApi"/>. Only <c>CanViewAuditLog</c> stays behind, because it
/// gates the TabItem's visibility and the TabItem itself remains the shell's markup.
/// </remarks>
public sealed partial class AuditTabViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;

    /// <summary>Routes messages to the shared bottom status bar.</summary>
    private readonly IShellContext _shell;

    public AuditTabViewModel(IShellContext shell) => _shell = shell;

    /// <summary>Set from whoami on login — gates the retention pencil and (via the ribbon) purge.</summary>
    [ObservableProperty] private bool _isTenantAdmin;

    public ObservableCollection<AuditEventRowViewModel> AuditEvents { get; } = [];

    [ObservableProperty] private string _auditActionFilter = string.Empty;
    [ObservableProperty] private DateTimeOffset? _auditFrom;
    [ObservableProperty] private DateTimeOffset? _auditTo;
    [ObservableProperty] private bool _auditHasMore;
    [ObservableProperty] private bool _auditBusy;
    [ObservableProperty] private string _auditVerifyStatus = string.Empty;
    [ObservableProperty] private bool _auditVerifyValid;
    [ObservableProperty] private bool _auditVerifyShown;
    // WORM-segment verification (ADR "Audit WORM segment verify").
    [ObservableProperty] private string _wormVerifyStatus = string.Empty;
    [ObservableProperty] private bool _wormVerifyValid;
    [ObservableProperty] private bool _wormVerifyShown;
    [ObservableProperty] private int _auditRetentionDays = 365;
    [ObservableProperty] private string _auditRetentionNote = string.Empty;
    private string? _auditNextCursor;

    public void SetApi(SimplArchiveApiClient api) => _api = api;

    /// <summary>Logout: back to the blank state, so the next session's user never sees this one's events.</summary>
    public void Reset()
    {
        AuditEvents.Clear();
        _auditNextCursor = null;
        AuditHasMore = false;
        AuditVerifyShown = false;
        WormVerifyShown = false;
        IsTenantAdmin = false;
    }

    /// <summary>Switching onto the tab: refresh retention, and load the first page once.</summary>
    public async Task ActivateAsync()
    {
        await LoadRetentionAsync();
        if (AuditEvents.Count == 0)
        {
            await LoadAuditPageAsync(reset: true);
        }
    }

    [RelayCommand]
    private Task RunAuditSearch() => LoadAuditPageAsync(reset: true);

    [RelayCommand]
    private async Task ClearAuditFilters()
    {
        AuditActionFilter = string.Empty;
        AuditFrom = null;
        AuditTo = null;
        await LoadAuditPageAsync(reset: true);
    }

    [RelayCommand]
    private Task LoadMoreAudit() => LoadAuditPageAsync(reset: false);

    private async Task LoadAuditPageAsync(bool reset)
    {
        if (_api is null)
        {
            return;
        }

        AuditBusy = true;
        try
        {
            if (reset)
            {
                AuditEvents.Clear();
                _auditNextCursor = null;
                AuditVerifyShown = false;
            }

            // "To" is inclusive of the whole selected day.
            var to = AuditTo is { } t ? new DateTimeOffset(t.Date.AddDays(1), TimeSpan.Zero) : (DateTimeOffset?)null;
            var page = await _api.Audit.GetAuditEventsAsync(
                string.IsNullOrWhiteSpace(AuditActionFilter) ? null : AuditActionFilter,
                AuditFrom,
                to,
                _auditNextCursor);

            foreach (var e in page.Events)
            {
                AuditEvents.Add(new AuditEventRowViewModel
                {
                    Timestamp = e.Timestamp,
                    ActorName = e.ActorName,
                    ActorType = e.ActorType,
                    Action = e.Action,
                    TargetType = e.TargetType,
                    TargetName = e.TargetName,
                    Details = e.Details,
                });
            }

            _auditNextCursor = page.NextCursor;
            AuditHasMore = _auditNextCursor is not null;
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrLoadAudit"), e.Message));
        }
        finally
        {
            AuditBusy = false;
        }
    }

    // Fetches the NDJSON export bytes for the current filters (the view saves them to a chosen file).
    public Task<byte[]>? ExportAuditBytesAsync()
    {
        if (_api is null)
        {
            return null;
        }

        var to = AuditTo is { } t ? new DateTimeOffset(t.Date.AddDays(1), TimeSpan.Zero) : (DateTimeOffset?)null;
        return _api.Audit.ExportAuditEventsAsync(string.IsNullOrWhiteSpace(AuditActionFilter) ? null : AuditActionFilter, AuditFrom, to);
    }

    [RelayCommand]
    private async Task VerifyAudit()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var result = await _api.Audit.VerifyAuditChainAsync();
            AuditVerifyValid = result.Valid;
            AuditVerifyStatus = result.Valid
                ? $"Chain intact ({result.CheckedCount} events)"
                : $"Tampering detected at #{result.BrokenAtSequence}";
            AuditVerifyShown = true;
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrVerifyAudit"), e.Message));
        }
    }

    [RelayCommand]
    private async Task VerifyWorm()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var result = await _api.Audit.VerifyAuditWormAsync();
            WormVerifyValid = result.Valid;
            WormVerifyStatus = result.Valid
                ? $"WORM sealed intact ({result.CheckedCount} events, {result.SegmentCount} segments)"
                : $"WORM {result.Reason} at #{result.BrokenAtSequence}";
            WormVerifyShown = true;
        }
        catch (Exception e)
        {
            Report(string.Format(Strings.Get("StErrVerifyWorm"), e.Message));
        }
    }

    private async Task LoadRetentionAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var retention = await _api.Audit.GetAuditRetentionAsync();
            AuditRetentionDays = retention.RetentionDays;
            AuditRetentionNote = retention.ChainStartSequence > 0
                ? $"Retained from #{retention.ChainStartSequence}" + (retention.LastPurgedAt is { } lp ? $" · last purged {lp.LocalDateTime:yyyy-MM-dd}" : "")
                : "";
        }
        catch (Exception)
        {
            // leave defaults
        }
    }

    [RelayCommand]
    private async Task SaveRetention()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var retention = await _api.Audit.SetAuditRetentionAsync(AuditRetentionDays);
            AuditRetentionDays = retention.RetentionDays;
            Report(Strings.Get("StAuditRetUpdated"));
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
        }
    }

    // Purges aged audit events for the tenant (the view confirms first). Returns the count purged.
    public async Task<int> PurgeAuditAsync()
    {
        if (_api is null)
        {
            return 0;
        }

        try
        {
            var result = await _api.Audit.PurgeAuditAsync();
            await LoadRetentionAsync();
            await LoadAuditPageAsync(reset: true);
            Report(string.Format(Strings.Get("StPurgedAudit"), result.PurgedCount));
            return result.PurgedCount;
        }
        catch (ApiActionException e)
        {
            Report(e.Message);
            return 0;
        }
    }

    // ---- Retention edit state (#530 tranche 9): read-only with a pencil, Save/Cancel in the same row
    // (ADR 0550). Cancel restores the value from before the pencil.
    [ObservableProperty] private bool _auditRetentionEditing;

    private int _auditRetentionSnapshot;

    [RelayCommand]
    private void BeginAuditRetentionEdit()
    {
        _auditRetentionSnapshot = AuditRetentionDays;
        AuditRetentionEditing = true;
    }

    [RelayCommand]
    private void CancelAuditRetentionEdit()
    {
        AuditRetentionDays = _auditRetentionSnapshot;
        AuditRetentionEditing = false;
    }

    [RelayCommand]
    private async Task SaveAuditRetentionEdit()
    {
        await SaveRetention();
        AuditRetentionEditing = false;
    }

    /// <summary>Fills the tab for <c>--screenshot --audit</c> — timestamps off the fixed demo clock (ADR 0510).</summary>
    internal void PopulateDemoForScreenshot()
    {
        IsTenantAdmin = true;
        AuditRetentionDays = 365;
        AuditVerifyStatus = "Chain intact (128 events)";
        AuditVerifyValid = true;
        AuditVerifyShown = true;
        WormVerifyStatus = "WORM sealed intact (96 events, 3 segments)";
        WormVerifyValid = true;
        WormVerifyShown = true;
        var now = MainWindowViewModel.ScreenshotClock;
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddMinutes(-2), ActorName = "Demo Admin", ActorType = "User", Action = "Auth.LoggedIn" });
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddMinutes(-9), ActorName = "Demo Admin", ActorType = "User", Action = "Document.Deleted", TargetType = "Document", TargetName = "Invoice 2025-001" });
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddMinutes(-15), ActorName = "Demo Admin", ActorType = "User", Action = "Acl.Granted", TargetType = "Document", TargetName = "Contracts", Details = "users …: CanSee, CanReadContent" });
        AuditEvents.Add(new AuditEventRowViewModel { Timestamp = now.AddHours(-1), ActorName = "Demo Admin", ActorType = "User", Action = "User.RightsChanged", TargetType = "User", TargetName = "Jane Doe", Details = "Manage repositories" });
    }

    /// <summary>
    /// Puts a message on the window's status line. Internal rather than private because this tab's own view
    /// reports through it — the view has the message, the tab owns the route.
    /// </summary>
    internal void Report(string message) => _shell.Report(message);
}
