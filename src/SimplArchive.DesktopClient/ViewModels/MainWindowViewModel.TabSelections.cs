using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The #530 tab selections + their screenshot populate, split out on arrival: MainWindowViewModel is on the
// 1000-line standing-debt list and its ceiling only shrinks — a new concern takes a home of its own instead
// of paying for its lines with a raised ceiling (the same rule that split the window code-behind, #466).
public sealed partial class MainWindowViewModel
{
    /// <summary>The task row the ribbon's Open acts on (#530, tranche 3).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasSelectedTaskRow))]
    private TaskItemViewModel? _selectedTaskRow;

    public bool HasSelectedTaskRow => SelectedTaskRow is not null;

    /// <summary>The retention row the ribbon acts on (#530, tranche 2) — single-select by decision.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasSelectedRetentionRow))]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(SelectedRetentionCanDispose))]
    private RetentionRowViewModel? _selectedRetentionRow;

    public bool HasSelectedRetentionRow => SelectedRetentionRow is not null;

    public bool SelectedRetentionCanDispose => SelectedRetentionRow?.CanDispose == true;

    // Populates the Retention tab for the headless screenshot (#530 tranche 2): one row per status, so the
    // render proves the row TEMPLATE, which no VM test can see.
    internal void PopulateRetentionDemoForScreenshot()
    {
        IsLoggedIn = true;
        RetentionItems.Clear();
        RetentionItems.Add(new RetentionRowViewModel(Guid.NewGuid(), "Framework agreement 2019", 7, "2026-05-01", true, false, null, null!));
        RetentionItems.Add(new RetentionRowViewModel(Guid.NewGuid(), "Invoice 2026-003", 7, "2033-01-14", false, false, null, null!));
        RetentionItems.Add(new RetentionRowViewModel(Guid.NewGuid(), "Disputed delivery note", 7, "2026-04-01", true, true, null, null!));
        SelectedRetentionRow = RetentionItems[0];
    }

    // Populates the Legal holds tab for the headless screenshot (#530 tranche 5): one active hold with items
    // (selected, so the detail + the ✕ rows render) and one released, so the render proves both row states.
    internal void PopulateLegalHoldsDemoForScreenshot()
    {
        IsLoggedIn = true;
        LegalHolds.Clear();
        SelectedHoldItems.Clear();
        var items = new List<Services.SimplArchiveApiClient.LegalHoldItemInfo>
        {
            new(Guid.NewGuid(), "Disputed delivery note"),
            new(Guid.NewGuid(), "Framework agreement 2019"),
        };
        var active = new Services.SimplArchiveApiClient.LegalHoldInfo(
            Guid.NewGuid(), "Case 2026-17 Meyer", "Pending litigation", new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero), true, items.Count, items);
        var released = new Services.SimplArchiveApiClient.LegalHoldInfo(
            Guid.NewGuid(), "Audit 2025", null, new DateTimeOffset(2025, 11, 5, 14, 0, 0, TimeSpan.Zero), false, 0, []);
        LegalHolds.Add(new LegalHoldRowViewModel(active.Id, active.Name, active.IsActive, active.ItemCount, active));
        LegalHolds.Add(new LegalHoldRowViewModel(released.Id, released.Name, released.IsActive, released.ItemCount, released));
        SelectedLegalHold = LegalHolds[0];
        foreach (var item in items)
        {
            SelectedHoldItems.Add(new LegalHoldItemRowViewModel(item.DocumentId, item.DocumentName, item));
        }
    }

    /// <summary>The catalog tag the ribbon acts on (#530, tranche 6) — single-select.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasSelectedTagRow))]
    private TagCatalogRow? _selectedTagRow;

    public bool HasSelectedTagRow => SelectedTagRow is not null;

    /// <summary>The ribbon's refresh — the load itself lives with the other tag commands.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task RefreshTagCatalog() => LoadTagCatalogAsync();

    // Populates the Tag catalog for the headless screenshot (#530 tranche 6): coloured, colourless and
    // selected rows, so the render proves the row template + the ribbon's greying.
    internal void PopulateTagsDemoForScreenshot()
    {
        IsLoggedIn = true;
        IsTenantAdmin = true;
        TagCatalogAdmin.Clear();
        TagCatalogAdmin.Add(new TagCatalogRow(new Services.SimplArchiveApiClient.TagCatalogItem(Guid.NewGuid(), "contract", "#2e7d32")));
        TagCatalogAdmin.Add(new TagCatalogRow(new Services.SimplArchiveApiClient.TagCatalogItem(Guid.NewGuid(), "invoice", "#1565c0")));
        TagCatalogAdmin.Add(new TagCatalogRow(new Services.SimplArchiveApiClient.TagCatalogItem(Guid.NewGuid(), "urgent", null)));
        SelectedTagRow = TagCatalogAdmin[0];
    }

    /// <summary>The My work dashboard's refresh (#530, tranche 7) — the web tab had one, this tab did not.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task RefreshMyWork() => LoadMyWorkAsync();

    /// <summary>The current version's workflow state (raw enum name), null when none was started — labels
    /// the workflow affordance by state (review decision: Start / Manage / View) without following the rel.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(WorkflowStateDisplay))]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(WorkflowButtonLabel))]
    private string? _sysWorkflowStatus;

    public string WorkflowStateDisplay => SysWorkflowStatus switch
    {
        null or "" => Strings.Get("WfNotStarted"),
        "Draft" => Strings.Get("WfStateDraft"),
        "InReview" => Strings.Get("WfStateInReview"),
        "Approved" => Strings.Get("WfStateApproved"),
        "Rejected" => Strings.Get("WfStateRejected"),
        "Released" => Strings.Get("WfStateReleased"),
        var other => other,
    };

    public string WorkflowButtonLabel => SysWorkflowStatus switch
    {
        null or "" => Strings.Get("CtxStartWorkflow"),
        "Released" => Strings.Get("CtxViewWorkflow"),
        _ => Strings.Get("CtxManageWorkflow"),
    };

    /// <summary>Greys the Search toolbar's Go to (#530, tranche 8); raised by OnSelectedSearchResultChanged.</summary>
    public bool HasSelectedSearchResult => SelectedSearchResult is not null;

    /// <summary>The audit-retention edit state (#530, tranche 9): read-only with a pencil, Save/Cancel in
    /// the same row (ADR 0550). Cancel restores the value from before the pencil.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _auditRetentionEditing;

    private int _auditRetentionSnapshot;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void BeginAuditRetentionEdit()
    {
        _auditRetentionSnapshot = AuditRetentionDays;
        AuditRetentionEditing = true;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CancelAuditRetentionEdit()
    {
        AuditRetentionDays = _auditRetentionSnapshot;
        AuditRetentionEditing = false;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task SaveAuditRetentionEdit()
    {
        await SaveRetention();
        AuditRetentionEditing = false;
    }

    // ---- Tenant settings, per group (#530 tranche 10, ADR "Per-group tenant settings") --------------------
    // ONE group edits at a time; each group's Save PUTs exactly its own fields via the api-client's generic
    // SaveTenantSettingsGroupAsync, following the settings-<group> rel of the last-READ settings.

    /// <summary>The settings resource as last read — its links carry the writable sub-resources.</summary>
    public Services.SimplArchiveApiClient.TenantSettingsInfo? LastTenantSettings { get; private set; }

    // ApplyTenantSettings (the big partial) is the one writer; a method rather than a public setter keeps it so.
    partial void OnTenantEditingGroupChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEditingTenantGeneral));
        OnPropertyChanged(nameof(IsEditingTenantCapture));
        OnPropertyChanged(nameof(IsEditingTenantSecurity));
        OnPropertyChanged(nameof(IsEditingTenantRecords));
        OnPropertyChanged(nameof(IsEditingTenantCheckout));
        OnPropertyChanged(nameof(IsEditingTenantStorage));
        OnPropertyChanged(nameof(IsEditingTenantExternalLinks));
        OnPropertyChanged(nameof(IsEditingTenantAuditStreaming));
        OnPropertyChanged(nameof(NoTenantGroupEditing));
    }

    /// <summary>The ONE group in edit mode, by its rel suffix — null when everything is read-only.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string? _tenantEditingGroup;

    public bool IsEditingTenantGeneral => TenantEditingGroup == "general";
    public bool IsEditingTenantCapture => TenantEditingGroup == "capture";
    public bool IsEditingTenantSecurity => TenantEditingGroup == "security";
    public bool IsEditingTenantRecords => TenantEditingGroup == "records";
    public bool IsEditingTenantCheckout => TenantEditingGroup == "checkout";
    public bool IsEditingTenantStorage => TenantEditingGroup == "storage";
    public bool IsEditingTenantExternalLinks => TenantEditingGroup == "external-links";
    public bool IsEditingTenantAuditStreaming => TenantEditingGroup == "audit-streaming";

    /// <summary>Hides the OTHER pencils while one group edits — starting a second edit would discard the first.</summary>
    public bool NoTenantGroupEditing => TenantEditingGroup is null;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void BeginTenantGroupEdit(string group) => TenantEditingGroup = group;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task CancelTenantGroupEdit() => LoadTenantSettingsAsync(); // resync from the server + leaves edit mode

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task SaveTenantGroupEdit(string group)
    {
        if (_api is null || LastTenantSettings is not { } settings)
        {
            return;
        }

        object body = group switch
        {
            "general" => new { name = TenantName.Trim() },
            "capture" => new
            {
                // Preserve the catalog order for the "+"-joined default (a stable OCR priority).
                defaultOcrLanguages = _ocrCatalog.Count > 0
                    ? string.Join('+', _ocrCatalog.Select(l => l.Code).Where(c => _tenantStagedOcrCodes.Contains(c)))
                    : string.Join('+', _tenantStagedOcrCodes),
                restrictTagsToCatalog = TenantRestrictTagsToCatalog,
            },
            "security" => new { requireMfa = TenantRequireMfa, allowPasskeyLogin = TenantAllowPasskeyLogin, enforceClearance = TenantEnforceClearance },
            "records" => new { auditRetentionDays = TenantAuditRetentionDays, wormLockMode = TenantWormLockModeIndex, requireDispositionReview = TenantRequireDispositionReview },
            "checkout" => new { checkoutTtlDays = TenantCheckoutTtlDays, checkoutWarningDays = TenantCheckoutWarningDays },
            "storage" => new
            {
                storageQuotaBytes = TenantStorageQuotaMb is { } mb ? (long?)((long)mb * 1024 * 1024) : null,
                incompleteUploadCleanupDays = TenantIncompleteUploadCleanupDays,
            },
            "external-links" => new { allowExternalLinks = TenantAllowExternalLinks, externalLinkMaxDays = TenantExternalLinkMaxDays, externalLinkDefaultAccesses = TenantExternalLinkDefaultAccesses, showExternalLinkUrl = TenantShowExternalLinkUrl },
            "audit-streaming" => new
            {
                auditWebhookUrl = string.IsNullOrWhiteSpace(TenantAuditWebhookUrl) ? null : TenantAuditWebhookUrl.Trim(),
                auditWebhookSecret = string.IsNullOrWhiteSpace(TenantAuditWebhookSecret) ? null : TenantAuditWebhookSecret,
            },
            _ => throw new InvalidOperationException($"Unknown settings group '{group}'."),
        };

        try
        {
            ApplyTenantSettings(await _api.SaveTenantSettingsGroupAsync(settings, group, body));
            TenantEditingGroup = null;
            Status = Strings.Get("StTenantSaved");
        }
        catch (Services.ApiActionException ex)
        {
            Status = ex.Message;
        }
    }
}
