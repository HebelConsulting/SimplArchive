using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;

namespace SimplArchive.Domain.Tenants;

public class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DeactivatedAt { get; set; }

    // The default OCR languages for this tenant's TIFF → searchable-PDF conversion (ADR "Per-tenant /
    // per-version OCR languages") — a Tesseract "+"-joined multi-select of OcrLanguages.Supported codes, used
    // when a DocumentVersion carries no override. NOT NULL; defaults to OcrLanguages.Default.
    public string DefaultOcrLanguages { get; set; } = OcrLanguages.Default;

    // Audit-log retention (ADR "Audit trail retention and purge"). Events older than this many days are purged
    // by the background worker / a manual admin purge. 0 = keep forever. Default 365.
    public int AuditRetentionDays { get; set; } = 365;

    // Stale-check-out auto-release (ADR "Stale check-out auto-release sweep"): a check-out idle for more than
    // this many days is auto-released by the background worker (lock cleared, cloud stash deleted, former
    // holder notified). 0 = disabled (never expire). Default 0 — opt-in per tenant.
    public int CheckoutTtlDays { get; set; }

    // Warn the check-out holder this many days before an idle check-out is auto-released (ADR "Check-out
    // expiry UX"), so they can check in or keep working. 0 = no warning. Default 1. Only meaningful when
    // CheckoutTtlDays > 0.
    public int CheckoutWarningDays { get; set; } = 1;

    // The S3 Object Lock retention mode for this tenant's WORM-immutable document versions (ADR "WORM /
    // immutable document versions (S3 Object Lock)"). Governance (default, dev-safe) vs Compliance (absolute).
    public WormLockMode WormLockMode { get; set; } = WormLockMode.Governance;

    // Tenant-wide require-MFA policy (ADR "MFA require-policy + TOTP secret encryption"): when true, a user in
    // this tenant must have MFA enrolled to sign in — the login page forces inline enrolment otherwise. Default
    // false (opt-in).
    public bool RequireMfa { get; set; }

    // Tenant-wide tag-catalog enforcement (ADR "Tag controlled vocabulary"): when true, a document tag PUT
    // rejects any tag not in the active TagDefinition catalog. Default false (free-form tagging, catalog curates).
    public bool RestrictTagsToCatalog { get; set; }

    // Tenant-wide passwordless passkey login policy (ADR "Passwordless passkey login"): when true, a user in this
    // tenant who has registered a passkey may sign in with it alone (no password, and the passkey satisfies the
    // require-MFA policy — a passkey with user verification is phishing-resistant multi-factor). Default false
    // (opt-in), since passwordless sign-in bypasses the password.
    public bool AllowPasskeyLogin { get; set; }

    // Records-retention disposition-review policy (ADR "Retention review-before-disposition"): when true, the
    // retention sweep does NOT auto-dispose expired documents — instead they wait in the Retention tab as a
    // review queue for a records manager (CanManageClassification) to Dispose or Extend. Default false, so
    // existing tenants keep the auto-disposition behavior (ADR "Retention policies (auto-disposition)").
    public bool RequireDispositionReview { get; set; }

    // Data-classification clearance enforcement (ADR "Sensitivity clearance enforcement"): when true, a
    // principal can't see/read a document whose sensitivity label's Rank exceeds their effective clearance
    // (own ⊔ groups) — the document is hidden from listings + search and a direct GET is denied. A tenant admin
    // bypasses it (like the ACL bypass). Default false, so upgrading changes no behavior — sensitivity labels
    // stay informational (ADRs 0399/0426/0428) until an admin turns this on.
    public bool EnforceClearance { get; set; }

    // The tamper-evidence hash chain's retained-window start (ADR "Audit trail retention and purge"): purge
    // deletes the oldest contiguous prefix, so verification walks from here instead of Sequence 0 — a purge
    // therefore isn't flagged as tampering, but an edit/deletion within retained events still is.
    // AuditChainStartPreviousHash is the hash the first retained event chains from (the genesis seed until a
    // purge captures the last-purged event's hash). Set only by the purge path; never manually.
    public long AuditChainStartSequence { get; set; }

    public string AuditChainStartPreviousHash { get; set; } = AuditChain.GenesisHash;

    public DateTimeOffset? AuditLastPurgedAt { get; set; }

    // Audit-log WORM archive checkpoint (ADR "Audit-log WORM"): the highest audit Sequence already sealed into
    // an immutable WORM segment in object storage for this tenant. -1 = nothing archived yet. Advanced by the
    // AuditWormArchiver as it seals contiguous runs of events.
    public long AuditWormArchivedThrough { get; set; } = -1;

    // Audit-webhook / SIEM streaming (ADR "Audit webhook streaming"). Null Url = disabled. The Secret is the
    // HMAC-SHA256 shared secret used to sign each delivery, encrypted at rest via OpenBao transit (ITransitEncryptor,
    // like TotpSecret). DeliveredThrough is the per-tenant checkpoint — the highest audit Sequence already POSTed to
    // the webhook — advanced by the AuditWebhookDispatcher; -1 = nothing delivered yet.
    public string? AuditWebhookUrl { get; set; }

    public string? AuditWebhookSecret { get; set; }

    public long AuditWebhookDeliveredThrough { get; set; } = -1;

    // Delivery retry/backoff + health (ADR "Audit webhook delivery retry/backoff"). On a failed send the dispatcher
    // increments the consecutive-failure count and schedules the next attempt with exponential-capped backoff
    // (NextAttemptAt); a success resets both. LastSuccessAt/LastFailureAt/LastError back the read-only health surface
    // on the Tenant tab.
    public int AuditWebhookConsecutiveFailures { get; set; }

    public DateTimeOffset? AuditWebhookNextAttemptAt { get; set; }

    public DateTimeOffset? AuditWebhookLastSuccessAt { get; set; }

    public DateTimeOffset? AuditWebhookLastFailureAt { get; set; }

    public string? AuditWebhookLastError { get; set; }

    // Per-tenant storage quota (ADR "Per-tenant storage quota"). Null = unlimited (production default). When set,
    // the version-finalize path refuses an upload that would push StorageUsedBytes past this limit
    // (409 STORAGE_QUOTA_EXCEEDED). Enforced app-level (portable across S3/SeaweedFS), not a native bucket quota.
    // External links (ADR 0546) — sharing a document with someone who has no account.
    //
    // Defaults to FALSE so an existing tenant is exactly as exposed after the migration as before it: an
    // unauthenticated content-serving surface should be an administrator's decision, not a side effect of an
    // upgrade. Checked at ACCESS time as well as at creation, which makes switching it off a genuine kill switch
    // for links already in the wild rather than merely a block on making new ones.
    public bool AllowExternalLinks { get; set; }

    // The furthest out a link may be set to expire, and the access count a link gets when the creator doesn't
    // choose one. Tenant-level so an administrator can tighten the rails to their own policy.
    public int ExternalLinkMaxDays { get; set; } = 180;

    public int ExternalLinkDefaultAccesses { get; set; } = 5;

    public long? StorageQuotaBytes { get; set; }

    // Maintained per-tenant used-storage counter (ADR "Per-tenant storage quota"): the sum of this tenant's
    // confirmed DocumentVersion blob sizes. Incremented when a version is confirmed (by DocumentVersion.SizeBytes),
    // decremented when a version's blob is purged. Only reflects blobs written since the feature landed — existing
    // versions predate DocumentVersion.SizeBytes and aren't counted (a recompute-from-storage action is deferred).
    public long StorageUsedBytes { get; set; }

    // Soft-quota warning de-dup level (ADR "Storage soft-quota warnings"): the highest warning threshold already
    // notified about — 0 = none, 1 = crossed 80%, 2 = crossed 95%. Raised (and the tenant's admins notified) when
    // usage first crosses a threshold; lowered without notifying when usage drops back below one, re-arming it.
    public int StorageWarningLevel { get; set; }

    // Per-tenant object-storage bucket lifecycle (ADR "Per-tenant bucket policy knobs"): abort incomplete
    // multipart uploads left dangling for this many days. 0 = disabled. Applied to the tenant's bucket at
    // provisioning + whenever this setting changes. Default 7. (No effect on today's single-PUT upload flow —
    // a demonstrable bucket-policy knob whose real effect runs on a lifecycle-capable backend, e.g. AWS S3.)
    public int IncompleteUploadCleanupDays { get; set; } = 7;
}
