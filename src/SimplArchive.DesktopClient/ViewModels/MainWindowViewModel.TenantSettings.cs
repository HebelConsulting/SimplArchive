using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The tenant-admin settings tab (ADR "Tenant-admin settings tab"): read-only until Edit, Save/Cancel in edit
// mode, gated on IsTenantAdmin. The webhook's configuration and health, the default OCR languages, the
// retention and storage figures, and the test-delivery action.
//
// The heading was ACCURATE for 223 of its 263 lines -- the fourth honest one found in this burn-down. What it
// had picked up at the END was CreateRepositoryAsync and the caller's own profile, neither of which is a
// tenant setting; the profile has gone to Principals, where its readers already were (#941).
public sealed partial class MainWindowViewModel
{
    // Read-only until Edit; Save/Cancel in edit mode. Gated on IsTenantAdmin (the tab's IsVisible).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasTenantSettings))] private bool _tenantSettingsLoaded;

    [ObservableProperty] private string _tenantName = string.Empty;
    [ObservableProperty] private int _tenantAuditRetentionDays;
    [ObservableProperty] private int _tenantCheckoutTtlDays;
    [ObservableProperty] private int _tenantCheckoutWarningDays = 1;
    // The WORM lock mode as a ComboBox SelectedIndex: 0 = Governance, 1 = Compliance.
    [ObservableProperty] private int _tenantWormLockModeIndex;
    [ObservableProperty] private bool _tenantRequireMfa;
    [ObservableProperty] private bool _tenantAllowPasskeyLogin;
    [ObservableProperty] private bool _tenantRequireDispositionReview;
    [ObservableProperty] private bool _tenantRestrictTagsToCatalog;
    // Data-classification clearance enforcement (ADR "Sensitivity clearance enforcement").
    [ObservableProperty] private bool _tenantEnforceClearance;

    // External links (ADR 0546, issue #385). The two caps only mean anything while the switch is on, so the UI
    // reveals them with it — one yes/no decision, then its bounds.
    // The tenant default a NEW user's IMAP show-all preference seeds from (#793) — not a permission.
    [ObservableProperty] private bool _tenantImapShowAllDocumentsDefault;

    [ObservableProperty] private bool _tenantAllowExternalLinks;

    // Whether an existing link's URL may be revealed again (issue #412). Threaded through EVERY site below:
    // the tenant-settings PUT is a FULL replacement, so a field missing from the call would silently reset it
    // — which is exactly the bug #404 fixed.
    [ObservableProperty] private bool _tenantShowExternalLinkUrl;
    [ObservableProperty] private int _tenantExternalLinkMaxDays = 180;
    [ObservableProperty] private int _tenantExternalLinkDefaultAccesses = 5;
    // Per-tenant storage quota (ADR "Per-tenant storage quota"): the editable limit in MB (null = unlimited) and a
    // read-only "used of limit" display line.
    [ObservableProperty] private int? _tenantStorageQuotaMb;
    [ObservableProperty] private string _tenantStorageUsage = string.Empty;
    [ObservableProperty] private string _tenantStorageWarning = string.Empty;
    // Per-tenant bucket lifecycle: abort incomplete multipart uploads after N days (0 = off, ADR "Per-tenant
    // bucket policy knobs").
    [ObservableProperty] private int _tenantIncompleteUploadCleanupDays;
    // Audit webhook / SIEM streaming (ADR "Audit webhook streaming"). The secret is write-only; the box is left
    // blank on load and a non-empty value (re)sets it. TenantWebhookConfigured reports whether one is stored.
    [ObservableProperty] private string _tenantAuditWebhookUrl = string.Empty;
    [ObservableProperty] private string _tenantAuditWebhookSecret = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TenantWebhookSecretStatus))]
    [NotifyPropertyChangedFor(nameof(TenantWebhookSecretWatermark))]
    private bool _tenantWebhookConfigured;

    public string TenantWebhookSecretStatus => TenantWebhookConfigured ? "Signing secret: configured" : "Signing secret: not set";
    public string TenantWebhookSecretWatermark => TenantWebhookConfigured ? "Leave blank to keep the current secret" : "Required to enable the webhook";

    // Read-only delivery health (ADR "Audit webhook delivery retry/backoff") shown when a webhook URL is set.
    private static readonly Avalonia.Media.IBrush HealthyBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2e7d32"));
    private static readonly Avalonia.Media.IBrush FailingBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e65100"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TenantWebhookHealthVisible))]
    [NotifyPropertyChangedFor(nameof(TenantWebhookHealthBrush))]
    private string _tenantWebhookHealth = string.Empty;
    public bool TenantWebhookHealthy { get; private set; }
    public bool TenantWebhookHealthVisible => !string.IsNullOrEmpty(TenantWebhookHealth);
    public Avalonia.Media.IBrush TenantWebhookHealthBrush => TenantWebhookHealthy ? HealthyBrush : FailingBrush;
    [ObservableProperty] private string _tenantOcrDisplay = string.Empty;
    [ObservableProperty] private string _tenantId = string.Empty;
    [ObservableProperty] private string _tenantStatus = string.Empty;
    [ObservableProperty] private string _tenantCreated = string.Empty;

    public bool HasTenantSettings => TenantSettingsLoaded;

    // The staged, ordered OCR codes while editing (edited via the same ordered picker as the detail pane).
    private List<string> _tenantStagedOcrCodes = [];

    [RelayCommand]
    public async Task LoadTenantSettingsAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var s = await _api.Admin.GetTenantSettingsAsync();
            ApplyTenantSettings(s);
            TenantEditingGroup = null;
            TenantSettingsLoaded = true;
            await LoadTenantModulesAsync(); // the Modules section rides the settings resource's rel (ADR 0543)
            await (_ocrLanguages?.EnsureLoadedAsync() ?? Task.CompletedTask);
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadTenant");
        }
    }

    private void ApplyTenantSettings(AdminClient.TenantSettingsInfo s)
    {
        LastTenantSettings = s; // group saves follow this resource's settings-<group> rels (ADR 0543)
        TenantName = s.Name;
        TenantAuditRetentionDays = s.AuditRetentionDays;
        TenantCheckoutTtlDays = s.CheckoutTtlDays;
        TenantCheckoutWarningDays = s.CheckoutWarningDays;
        TenantWormLockModeIndex = s.WormLockMode;
        TenantRequireMfa = s.RequireMfa;
        TenantAllowPasskeyLogin = s.AllowPasskeyLogin;
        TenantRequireDispositionReview = s.RequireDispositionReview;
        TenantRestrictTagsToCatalog = s.RestrictTagsToCatalog;
        TenantEnforceClearance = s.EnforceClearance;
        TenantImapShowAllDocumentsDefault = s.ImapShowAllDocumentsDefault;
        TenantAllowExternalLinks = s.AllowExternalLinks;
        TenantShowExternalLinkUrl = s.ShowExternalLinkUrl;
        TenantExternalLinkMaxDays = s.ExternalLinkMaxDays;
        TenantExternalLinkDefaultAccesses = s.ExternalLinkDefaultAccesses;
        TenantStorageQuotaMb = s.StorageQuotaBytes is { } b ? (int)(b / (1024 * 1024)) : null;
        if (s.StorageQuotaBytes is { } quota && quota > 0)
        {
            // Soft-quota indicator (ADR "Storage soft-quota warnings") — matches the server's 80%/95% thresholds.
            var pct = (int)(s.StorageUsedBytes * 100 / quota);
            TenantStorageUsage = $"Used: {FormatBytes(s.StorageUsedBytes)} of {FormatBytes(quota)} ({pct}%)";
            TenantStorageWarning = pct >= 95 ? "Almost full" : pct >= 80 ? "Approaching quota" : "";
        }
        else
        {
            TenantStorageUsage = "Used: " + FormatBytes(s.StorageUsedBytes) + " (no limit)";
            TenantStorageWarning = string.Empty;
        }
        TenantIncompleteUploadCleanupDays = s.IncompleteUploadCleanupDays;
        TenantAuditWebhookUrl = s.AuditWebhookUrl ?? "";
        TenantAuditWebhookSecret = string.Empty;
        TenantWebhookConfigured = s.AuditWebhookConfigured;
        TenantWebhookHealthy = s.AuditWebhookConsecutiveFailures == 0;
        TenantWebhookHealth = DescribeWebhookHealth(s);
        _tenantStagedOcrCodes = s.DefaultOcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        TenantOcrDisplay = (_ocrLanguages?.Describe(_tenantStagedOcrCodes) ?? "");
        TenantId = s.Id.ToString();
        TenantStatus = s.Status;
        TenantCreated = s.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    // Bytes → a human "N.N MB" / "N.N GB" for the storage-usage line (ADR "Per-tenant storage quota").
    private static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.##} GB"
        : $"{bytes / (double)(1L << 20):0.##} MB";

    // The read-only webhook-delivery health line (ADR "Audit webhook delivery retry/backoff"); empty when no
    // webhook is configured.
    private static string DescribeWebhookHealth(AdminClient.TenantSettingsInfo s)
    {
        if (string.IsNullOrEmpty(s.AuditWebhookUrl))
        {
            return "";
        }

        static string When(DateTimeOffset? t) => t is { } v ? v.LocalDateTime.ToString("g") : "";

        if (s.AuditWebhookConsecutiveFailures == 0)
        {
            return s.AuditWebhookLastSuccessAt is { } ok
                ? $"Delivery: healthy — last success {When(ok)}"
                : "Delivery: healthy";
        }

        var plural = s.AuditWebhookConsecutiveFailures == 1 ? "failure" : "failures";
        var error = s.AuditWebhookLastError is { Length: > 0 } e ? $" ({e})" : "";
        var next = s.AuditWebhookNextAttemptAt is { } n ? $"; next retry {When(n)}" : "";
        var last = s.AuditWebhookLastSuccessAt is { } ls ? $"; last success {When(ls)}" : "; never delivered";
        return $"Delivery: failing — {s.AuditWebhookConsecutiveFailures} consecutive {plural}{error}{next}{last}";
    }

    [RelayCommand]
    private async Task RecomputeStorage()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            ApplyTenantSettings(await _api.Admin.RecomputeStorageAsync());
            Status = Strings.Get("StStorageRecomputed");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRecompute");
        }
    }

    [RelayCommand]
    private async Task TestWebhook()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var (success, error) = await _api.Admin.TestAuditWebhookAsync();
            Status = success ? "Test event delivered successfully." : $"Test delivery failed: {error ?? "unknown error"}";
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrTestEvent");
        }
    }

    // The tenant-default OCR ordered picker state + staging (edited via the shared OcrLanguagePickerDialog).
    public (IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Catalog, IReadOnlyList<string> Selected) TenantOcrPickerState() =>
        (_ocrLanguages?.Options ?? [], _tenantStagedOcrCodes);

    public void StageTenantOcrLanguages(IReadOnlyList<string> codes)
    {
        _tenantStagedOcrCodes = codes.ToList();
        TenantOcrDisplay = (_ocrLanguages?.Describe(_tenantStagedOcrCodes) ?? "");
    }
}
