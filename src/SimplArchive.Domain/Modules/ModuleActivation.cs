using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Modules;

/// <summary>
/// One tenant's activation of one industry module (ADR 0740): the row a verified license upserts. At most
/// one per (tenant, module) — renewal replaces the end date and the license pointer; history lives in the
/// filed license documents and the audit trail, never in extra rows.
/// </summary>
/// <remarks>
/// Whether the module is currently ACTIVE is never stored — it derives from
/// <see cref="SupportContractEndDate"/> plus the grace period at every ask (the epic's derived-status
/// philosophy, ADR 0742 applied to licensing itself): the module "turns off" the instant the math says so,
/// with no sweep to schedule and no flag to go stale.
/// </remarks>
public class ModuleActivation : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The module's stable identity (<c>IIndustryModule.ModuleId</c>) — a string, because the
    /// module is not an entity here: it is code, loaded or not, and this row must survive it being either.</summary>
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>The support contract's last day, inclusive, at midnight UTC (from the license claim).
    /// The behaviour runs through this day plus the grace period, then deactivates (ADR 0740).</summary>
    public DateTimeOffset SupportContractEndDate { get; set; }

    /// <summary>
    /// The filed license document this activation rests on. A plain column, not a FK — the
    /// <c>Document.CurrentVersionId</c> precedent (ADR 0503): the license document lives wherever the
    /// administrator filed it, and its deletion must not cascade into (or be blocked by) this row.
    /// </summary>
    public Guid LicenseDocumentId { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    /// <summary>The tenant administrator who filed the (latest) license; null when a platform actor did.</summary>
    public Guid? ActivatedByUserId { get; set; }

    /// <summary>
    /// The highest escalation step already announced (0 calm · 1 expiring soon · 2 in grace ·
    /// 3 deactivated) — the storage-warning-level shape: the sweep notifies the tenant's admins on an
    /// upward cross and a renewal recomputes it downward silently, so each step is announced exactly once.
    /// </summary>
    public int EscalationLevel { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
