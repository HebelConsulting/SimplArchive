using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Ocr;
using SimplArchive.Api.Errors.Exceptions.Tenant;
using SimplArchive.Api.Errors.Exceptions.Storage;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The current tenant's self-service settings (ADR "Tenant-admin settings tab"): read + edit the editable
/// Tenant columns (Name, DefaultOcrLanguages, AuditRetentionDays) plus read-only reference (Id/Status/CreatedAt).
/// Tenant-admin only (a User with IsTenantAdmin, own or via a group; a ServiceAccount never is). Distinct from
/// the platform-admin `api/tenants` CRUD — this is a tenant acting on itself.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tenant-settings")]
[Authorize]
public class TenantSettingsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly ITransitEncryptor _transit;
    private readonly IObjectStorageClient _objectStorage;
    private readonly IAuditWebhookSender _webhookSender;
    private readonly IAuditRecorder _audit;

    public TenantSettingsController(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        ITransitEncryptor transit,
        IObjectStorageClient objectStorage,
        IAuditWebhookSender webhookSender,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _transit = transit;
        _objectStorage = objectStorage;
        _webhookSender = webhookSender;
        _audit = audit;
    }

    public class TenantSettingsResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string DefaultOcrLanguages { get; set; } = "";
        public int AuditRetentionDays { get; set; }
        public int CheckoutTtlDays { get; set; }
        public int CheckoutWarningDays { get; set; }
        public WormLockMode WormLockMode { get; set; }
        public bool RequireMfa { get; set; }
        public bool AllowPasskeyLogin { get; set; }
        public bool RequireDispositionReview { get; set; }
        // External links (ADR 0546). AllowExternalLinks is the tenant's master switch for sharing a document with
        // people who have no account — read at ACCESS time, so turning it off stops links already in the wild.
        public bool AllowExternalLinks { get; set; }
        public int ExternalLinkMaxDays { get; set; }
        public int ExternalLinkDefaultAccesses { get; set; }

        /// <summary>Whether an existing link's URL may be revealed again after creation (issue #412).</summary>
        public bool ShowExternalLinkUrl { get; set; }

        // Tag-catalog enforcement (ADR "Tag controlled vocabulary").
        public bool RestrictTagsToCatalog { get; set; }
        // Data-classification clearance enforcement (ADR "Sensitivity clearance enforcement").
        public bool EnforceClearance { get; set; }
        // Per-tenant storage quota (ADR "Per-tenant storage quota"): the limit in bytes (null = unlimited) and the
        // read-only maintained used-storage counter.
        public long? StorageQuotaBytes { get; set; }
        public long StorageUsedBytes { get; set; }
        // Per-tenant bucket lifecycle (ADR "Per-tenant bucket policy knobs"): abort incomplete multipart uploads
        // after this many days (0 = disabled).
        public int IncompleteUploadCleanupDays { get; set; }
        // Audit webhook / SIEM streaming (ADR "Audit webhook streaming"). The secret is never returned;
        // AuditWebhookConfigured just reports whether one is set.
        public string? AuditWebhookUrl { get; set; }
        public bool AuditWebhookConfigured { get; set; }
        // Read-only delivery health (ADR "Audit webhook delivery retry/backoff"): backs a status line on the
        // Tenant tab. Not settable.
        public int AuditWebhookConsecutiveFailures { get; set; }
        public DateTimeOffset? AuditWebhookLastSuccessAt { get; set; }
        public DateTimeOffset? AuditWebhookLastFailureAt { get; set; }
        public DateTimeOffset? AuditWebhookNextAttemptAt { get; set; }
        public string? AuditWebhookLastError { get; set; }
    }

    public class UpdateTenantSettingsRequest
    {
        public string Name { get; set; } = "";
        public string DefaultOcrLanguages { get; set; } = "";
        public int AuditRetentionDays { get; set; }
        public int CheckoutTtlDays { get; set; }
        public int CheckoutWarningDays { get; set; }
        public WormLockMode WormLockMode { get; set; }
        public bool RequireMfa { get; set; }
        public bool AllowPasskeyLogin { get; set; }
        public bool RequireDispositionReview { get; set; }
        // External links (ADR 0546). AllowExternalLinks is the tenant's master switch for sharing a document with
        // people who have no account — read at ACCESS time, so turning it off stops links already in the wild.
        public bool AllowExternalLinks { get; set; }
        public int ExternalLinkMaxDays { get; set; }
        public int ExternalLinkDefaultAccesses { get; set; }

        /// <summary>Whether an existing link's URL may be revealed again after creation (issue #412).</summary>
        public bool ShowExternalLinkUrl { get; set; }

        public bool RestrictTagsToCatalog { get; set; }
        // Data-classification clearance enforcement (ADR "Sensitivity clearance enforcement").
        public bool EnforceClearance { get; set; }
        // Per-tenant storage quota in bytes; null = unlimited (ADR "Per-tenant storage quota").
        public long? StorageQuotaBytes { get; set; }
        // Abort incomplete multipart uploads after this many days; 0 = disabled (ADR "Per-tenant bucket policy knobs").
        public int IncompleteUploadCleanupDays { get; set; }
        public string? AuditWebhookUrl { get; set; }
        // Write-only: a non-empty value (re)sets the signing secret; null/empty keeps the existing one.
        public string? AuditWebhookSecret { get; set; }
    }

    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == _currentTenantAccessor.TenantId, cancellationToken);
        return tenant is null ? NotFound() : Ok(ToResource(tenant));
    }

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        await IsTenantAdminAsync(cancellationToken) ? NoContent() : Forbid();

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateTenantSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == _currentTenantAccessor.TenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        // Snapshot the current values before mutating, so the audit event can carry field-level before→after
        // changes (ADR "Audit tenant-settings, inbox filing + personal-repository creation"). The webhook secret
        // itself is never captured — only whether one is set — so it can't leak into the audit log.
        var before = SettingsSnapshot.From(tenant);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new TenantNameRequiredException();
        }

        if (request.AuditRetentionDays < 0)
        {
            throw new InvalidRetentionException();
        }

        if (request.CheckoutTtlDays < 0)
        {
            throw new InvalidCheckoutTtlException();
        }

        if (request.CheckoutWarningDays < 0)
        {
            throw new InvalidCheckoutWarningException();
        }

        if (!Enum.IsDefined(request.WormLockMode))
        {
            throw new InvalidWormModeException();
        }

        if (request.StorageQuotaBytes is < 0)
        {
            throw new InvalidStorageQuotaException();
        }

        if (request.IncompleteUploadCleanupDays < 0)
        {
            throw new InvalidUploadCleanupException();
        }

        // OCR languages: a "+"-joined selection of the supported catalog codes; empty falls back to the default.
        var ocr = string.IsNullOrWhiteSpace(request.DefaultOcrLanguages) ? OcrLanguages.Default : request.DefaultOcrLanguages.Trim();
        var known = OcrLanguages.Supported.Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        var codes = ocr.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.Length == 0)
        {
            throw UnknownOcrLanguageException.Required();
        }

        if (codes.FirstOrDefault(c => !known.Contains(c)) is { } unknown)
        {
            throw UnknownOcrLanguageException.Unsupported(unknown);
        }

        // Audit webhook (ADR "Audit webhook streaming"): validate + set the URL; the secret is encrypted at rest.
        var webhookUrl = string.IsNullOrWhiteSpace(request.AuditWebhookUrl) ? null : request.AuditWebhookUrl.Trim();
        if (webhookUrl is not null && !(Uri.TryCreate(webhookUrl, UriKind.Absolute, out var wu) && (wu.Scheme == Uri.UriSchemeHttp || wu.Scheme == Uri.UriSchemeHttps)))
        {
            throw new InvalidWebhookUrlException();
        }

        if (webhookUrl is null)
        {
            tenant.AuditWebhookUrl = null;
            tenant.AuditWebhookSecret = null; // clearing the URL clears the secret
        }
        else
        {
            tenant.AuditWebhookUrl = webhookUrl;
            if (!string.IsNullOrEmpty(request.AuditWebhookSecret))
            {
                tenant.AuditWebhookSecret = await _transit.EncryptAsync(request.AuditWebhookSecret);
            }
            else if (tenant.AuditWebhookSecret is null)
            {
                throw new WebhookSecretRequiredException();
            }
        }

        tenant.Name = request.Name.Trim();
        tenant.DefaultOcrLanguages = string.Join('+', codes);
        tenant.AuditRetentionDays = request.AuditRetentionDays;
        tenant.CheckoutTtlDays = request.CheckoutTtlDays;
        tenant.CheckoutWarningDays = request.CheckoutWarningDays;
        tenant.WormLockMode = request.WormLockMode;
        tenant.RequireMfa = request.RequireMfa;
        tenant.AllowPasskeyLogin = request.AllowPasskeyLogin;
        tenant.RestrictTagsToCatalog = request.RestrictTagsToCatalog;
        tenant.RequireDispositionReview = request.RequireDispositionReview;
        tenant.EnforceClearance = request.EnforceClearance;
        tenant.AllowExternalLinks = request.AllowExternalLinks;
        tenant.ExternalLinkMaxDays = request.ExternalLinkMaxDays;
        tenant.ExternalLinkDefaultAccesses = request.ExternalLinkDefaultAccesses;
        tenant.ShowExternalLinkUrl = request.ShowExternalLinkUrl;
        tenant.StorageQuotaBytes = request.StorageQuotaBytes; // null = unlimited
        var lifecycleChanged = tenant.IncompleteUploadCleanupDays != request.IncompleteUploadCleanupDays;
        tenant.IncompleteUploadCleanupDays = request.IncompleteUploadCleanupDays;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Tenant.Name's partial unique index among Active tenants (ADR "Tenant name uniqueness").
            throw TenantNameConflictException.OnRename();
        }

        // Re-apply the bucket lifecycle policy when it changed (ADR "Per-tenant bucket policy knobs"). Best-effort:
        // a lifecycle failure on a non-lifecycle backend must not fail the settings save.
        if (lifecycleChanged && _currentTenantAccessor.TenantId is { } lifecycleTenantId)
        {
            try
            {
                await _objectStorage.SetBucketLifecycleAsync(lifecycleTenantId, tenant.IncompleteUploadCleanupDays, cancellationToken);
            }
            catch (Exception)
            {
                // Logged upstream; the setting is persisted regardless.
            }
        }

        // Audit the change with a field-level before→after summary (ADR "Audit tenant-settings, inbox filing +
        // personal-repository creation"). A no-op PUT (nothing actually changed) isn't recorded — no audit noise.
        var changes = SettingsSnapshot.Diff(before, SettingsSnapshot.From(tenant), secretProvided: !string.IsNullOrEmpty(request.AuditWebhookSecret));
        if (changes.Count > 0)
        {
            await _audit.RecordAsync(AuditActions.TenantSettingsUpdated, "Tenant", tenant.Id, tenant.Name, string.Join("; ", changes), cancellationToken: cancellationToken);
        }

        return Ok(ToResource(tenant));
    }

    // A snapshot of the auditable settings — everything the PUT can change except the webhook secret's *value*
    // (only whether one is set is captured, so the secret can never leak into the audit log).
    private sealed record SettingsSnapshot(
        string Name, string DefaultOcrLanguages, int AuditRetentionDays, int CheckoutTtlDays, int CheckoutWarningDays,
        WormLockMode WormLockMode, bool RequireMfa, bool AllowPasskeyLogin, bool RequireDispositionReview,
        bool RestrictTagsToCatalog, bool EnforceClearance,
        long? StorageQuotaBytes, int IncompleteUploadCleanupDays, string? AuditWebhookUrl, bool HasWebhookSecret)
    {
        public static SettingsSnapshot From(Tenant t) => new(
            t.Name, t.DefaultOcrLanguages, t.AuditRetentionDays, t.CheckoutTtlDays, t.CheckoutWarningDays,
            t.WormLockMode, t.RequireMfa, t.AllowPasskeyLogin, t.RequireDispositionReview,
            t.RestrictTagsToCatalog, t.EnforceClearance,
            t.StorageQuotaBytes, t.IncompleteUploadCleanupDays, t.AuditWebhookUrl, t.AuditWebhookSecret is not null);

        // A human-readable list of "Field a→b" changes; empty when nothing changed. `secretProvided` distinguishes
        // "the URL/secret-presence didn't change" from "the same URL was saved with a fresh secret".
        public static List<string> Diff(SettingsSnapshot a, SettingsSnapshot b, bool secretProvided)
        {
            var changes = new List<string>();
            // Scalar: quote strings, ToString numbers/enums. Text: append pre-formatted display strings verbatim.
            void Scalar(string label, object? from, object? to)
            {
                if (!Equals(from, to)) changes.Add($"{label} {Show(from)}→{Show(to)}");
            }
            void Text(string label, string from, string to)
            {
                if (from != to) changes.Add($"{label} {from}→{to}");
            }

            Scalar("Name", a.Name, b.Name);
            Scalar("Default OCR languages", a.DefaultOcrLanguages, b.DefaultOcrLanguages);
            Scalar("Audit retention days", a.AuditRetentionDays, b.AuditRetentionDays);
            Scalar("Check-out TTL days", a.CheckoutTtlDays, b.CheckoutTtlDays);
            Scalar("Check-out warning days", a.CheckoutWarningDays, b.CheckoutWarningDays);
            Scalar("WORM lock mode", a.WormLockMode, b.WormLockMode);
            Text("Require MFA", OnOff(a.RequireMfa), OnOff(b.RequireMfa));
            Text("Allow passkey login", OnOff(a.AllowPasskeyLogin), OnOff(b.AllowPasskeyLogin));
            Text("Restrict tags to catalog", OnOff(a.RestrictTagsToCatalog), OnOff(b.RestrictTagsToCatalog));
            Text("Require disposition review", OnOff(a.RequireDispositionReview), OnOff(b.RequireDispositionReview));
            Text("Enforce clearance", OnOff(a.EnforceClearance), OnOff(b.EnforceClearance));
            Text("Storage quota", Quota(a.StorageQuotaBytes), Quota(b.StorageQuotaBytes));
            Scalar("Incomplete-upload cleanup days", a.IncompleteUploadCleanupDays, b.IncompleteUploadCleanupDays);
            Text("Audit webhook URL", a.AuditWebhookUrl ?? "(none)", b.AuditWebhookUrl ?? "(none)");

            // The secret is redacted: report only presence changes, plus an explicit "rotated" note when a fresh
            // secret was supplied for an already-configured webhook.
            if (a.HasWebhookSecret != b.HasWebhookSecret)
            {
                changes.Add($"Audit webhook secret {(b.HasWebhookSecret ? "set" : "cleared")}");
            }
            else if (secretProvided && b.HasWebhookSecret)
            {
                changes.Add("Audit webhook secret rotated");
            }

            return changes;
        }

        private static string Show(object? v) => v switch { null => "(none)", string s => $"'{s}'", _ => v.ToString() ?? "" };
        private static string OnOff(bool v) => v ? "on" : "off";
        private static string Quota(long? bytes) => bytes is { } b ? $"{b} bytes" : "unlimited";
    }

    // Rebuilds the tenant's maintained used-storage counter from the actual confirmed version blobs (ADR
    // "Per-tenant storage quota") — fixes tenants whose blobs predate the quota feature (no SizeBytes) or drifted.
    // Backfills any missing DocumentVersion.SizeBytes by HEADing the blob, then sets StorageUsedBytes to the sum.
    // Tenant-admin only. All confirmed versions count (a soft-deleted document's blob still occupies storage until
    // purge; DocumentVersion has no soft-delete filter, so they're included).
    [HttpPost("recompute-storage")]
    public async Task<IActionResult> RecomputeStorage(CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == _currentTenantAccessor.TenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        var versions = await _dbContext.DocumentVersions
            .Where(v => v.Status == DocumentVersionStatus.Confirmed)
            .ToListAsync(cancellationToken);

        long total = 0;
        foreach (var version in versions)
        {
            if (version.SizeBytes is not { } size)
            {
                try
                {
                    size = await _objectStorage.GetObjectSizeAsync(version.ObjectKey, cancellationToken);
                }
                catch (Exception)
                {
                    size = 0; // an orphaned/missing blob contributes nothing
                }

                version.SizeBytes = size; // backfill for future precise purge accounting
            }

            total += size;
        }

        tenant.StorageUsedBytes = total;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(ToResource(tenant));
    }

    public class TestWebhookResponse : HypermediaResource
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }

    // Sends a one-off synthetic audit event to the tenant's saved SIEM webhook (ADR "Audit webhook test delivery")
    // so an admin can verify the endpoint + signature before real events flow. Uses the real audit-event line shape
    // (ADR "Audit trail export") with a marked Webhook.Test action + Sequence -1, HMAC-SHA256-signed with the
    // stored secret — the same signing the dispatcher does. Returns the delivery outcome (200 even on a failed
    // delivery: the request succeeded; Success/Error report whether the endpoint accepted it). Tenant-admin only.
    [HttpPost("audit-webhook/test")]
    public async Task<IActionResult> TestAuditWebhook(CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == _currentTenantAccessor.TenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(tenant.AuditWebhookUrl) || tenant.AuditWebhookSecret is null)
        {
            throw new WebhookNotConfiguredException();
        }

        var line = new
        {
            sequence = -1L,
            hash = (string?)null,
            timestamp = DateTimeOffset.UtcNow,
            actorType = "System",
            actorId = Guid.Empty,
            actorName = "SimplArchive test delivery",
            action = "Webhook.Test",
            targetType = (string?)null,
            targetId = (Guid?)null,
            targetName = (string?)null,
            details = "This is a test event to verify your SimplArchive audit webhook configuration.",
        };

        var body = System.Text.Json.JsonSerializer.Serialize(line, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var secret = await _transit.DecryptAsync(tenant.AuditWebhookSecret);
        var signature = Convert.ToHexStringLower(
            System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(body)));

        var result = await _webhookSender.SendAsync(tenant.AuditWebhookUrl, body, signature, cancellationToken);
        return Ok(new TestWebhookResponse { Success = result.Success, Error = result.Error, Links = [new Link("self", "/api/tenant-settings", "GET")] });
    }

    private TenantSettingsResource ToResource(Tenant tenant) => new()
    {
        Id = tenant.Id,
        Name = tenant.Name,
        Status = tenant.Status.ToString(),
        CreatedAt = tenant.CreatedAt,
        DefaultOcrLanguages = tenant.DefaultOcrLanguages,
        AuditRetentionDays = tenant.AuditRetentionDays,
        CheckoutTtlDays = tenant.CheckoutTtlDays,
        CheckoutWarningDays = tenant.CheckoutWarningDays,
        WormLockMode = tenant.WormLockMode,
        RequireMfa = tenant.RequireMfa,
        AllowPasskeyLogin = tenant.AllowPasskeyLogin,
        RestrictTagsToCatalog = tenant.RestrictTagsToCatalog,
        RequireDispositionReview = tenant.RequireDispositionReview,
        AllowExternalLinks = tenant.AllowExternalLinks,
        ExternalLinkMaxDays = tenant.ExternalLinkMaxDays,
        ExternalLinkDefaultAccesses = tenant.ExternalLinkDefaultAccesses,
        ShowExternalLinkUrl = tenant.ShowExternalLinkUrl,
        EnforceClearance = tenant.EnforceClearance,
        StorageQuotaBytes = tenant.StorageQuotaBytes,
        StorageUsedBytes = tenant.StorageUsedBytes,
        IncompleteUploadCleanupDays = tenant.IncompleteUploadCleanupDays,
        AuditWebhookUrl = tenant.AuditWebhookUrl,
        AuditWebhookConfigured = tenant.AuditWebhookSecret is not null,
        AuditWebhookConsecutiveFailures = tenant.AuditWebhookConsecutiveFailures,
        AuditWebhookLastSuccessAt = tenant.AuditWebhookLastSuccessAt,
        AuditWebhookLastFailureAt = tenant.AuditWebhookLastFailureAt,
        AuditWebhookNextAttemptAt = tenant.AuditWebhookNextAttemptAt,
        AuditWebhookLastError = tenant.AuditWebhookLastError,
        Links = [new Link("self", "/api/tenant-settings", "GET")],
    };
}
