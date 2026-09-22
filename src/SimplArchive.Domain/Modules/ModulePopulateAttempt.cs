using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Modules;

/// <summary>
/// The populate hook's last-attempt clock for one (machine, subject folder) — the rate limit for a hook
/// whose collection holds DURABLE items (ABI 0.28, issue #1307).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The hook's built-in cooldown is the staged content's own <c>ExpiresAt</c>
/// (ADR 0810): no table, nothing to go stale — but nothing to engage either when the collection's items are
/// durable (a calendar of real entries carries no expiry), so a polling CalDAV client would reach the
/// module's upstream source on every poll: the unattended-scraper shape ADR 0756 rejected. For a transition
/// that declares a minimum refresh interval, this row is the clock the interval is measured against.
/// </para>
/// <para>
/// <b>Written on every ATTEMPT, success or failure</b> — the outbound request is the thing being limited,
/// and a failing source hammered once per poll is exactly as impolite as a healthy one.
/// </para>
/// <para>
/// <b>Concurrency-UNTRACKED, by granted exemption (2026-09-22)</b>: machine-owned protocol bookkeeping —
/// only the populate paths upsert it, no user edits it, and two concurrent upserts of "now" have no update
/// worth losing. If this entity ever grows a user-facing edit surface, that exemption lapses with it.
/// </para>
/// </remarks>
public class ModulePopulateAttempt : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The declared machine whose auto-refresh transition attempted the fetch.</summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>The subject folder — FK to Documents with cascade delete, so the clock dies with it.</summary>
    public Guid SubjectDocumentId { get; set; }

    public DateTimeOffset LastAttemptAt { get; set; }
}
