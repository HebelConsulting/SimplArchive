using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Modules;

/// <summary>
/// A module content SOURCE that is currently failing to refresh — one row per (module, machine, subject),
/// present only while it is broken (ADR 0811).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A populate hook (ADR 0756) fetches on demand and degrades to "serve what is
/// already filed" when the fetch fails — deliberately, because a failed enrichment must not break somebody
/// else's read. The cost of that choice is silence: the tenant sees an empty or stale folder and the only
/// record is a log line in the OPERATOR's collector, which a tenant administrator has no access to. So a
/// weather feed can be dead for a week inside a healthy-looking installation. This row is what makes that
/// state answerable, and what <see cref="ConsecutiveFailures"/> crossing the threshold notifies on.
/// </para>
/// <para>
/// <b>ABSENCE MEANS HEALTHY.</b> A success deletes the row rather than zeroing it, so there is no "stale
/// zero" to misread and no second state meaning the same thing. It also keeps the table proportional to what
/// is broken rather than to what exists — on a tenant where everything works it is empty.
/// </para>
/// <para>
/// <b>Per SOURCE, not per module</b>, and this is the load-bearing choice rather than a detail. Counting
/// consecutive failures per module lets one permanently-broken source hide behind its healthy siblings: an
/// aerodrome that has been failing for a week resets the counter every time a different folder succeeds, so
/// "three consecutive" never arrives. The notification still speaks about the module — that is the thing an
/// administrator acts on — but the counting happens where the failure is.
/// </para>
/// <para>
/// <b>This row is deliberately NOT <see cref="IConcurrencyTracked"/>.</b> It is machine-owned bookkeeping: no
/// human edits it, no form loads it, and its only writers are the populate paths. There is no user edit to
/// lose, which is the collision a token exists to detect. What two instances CAN race is the threshold
/// crossing, and a token would not help there — that is handled by writing <see cref="NotifiedAt"/> under a
/// conditional update, so the notification is sent by whichever writer wins and once in total.
/// </para>
/// </remarks>
public class ModuleContentHealth : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The assembly-keyed module id, as on <see cref="ModuleActivation"/>.</summary>
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>The declaring machine, so two machines over one folder are counted apart.</summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// The folder the hook populates. Deliberately a plain column rather than a foreign key: the row is a
    /// record ABOUT a document, and a cascade would be the wrong shape — deleting the folder should drop this
    /// silently (it does, via the sweep below), not participate in the document's delete graph.
    /// </summary>
    public Guid SubjectDocumentId { get; set; }

    /// <summary>How many times in a row the hook has failed. Reset by deletion, never by assignment.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When this episode began — what the notification quotes, because "failing since 14:05" is the
    /// fact an administrator can act on, where a count alone is not.</summary>
    public DateTimeOffset FirstFailureAt { get; set; }

    public DateTimeOffset LastFailureAt { get; set; }

    /// <summary>
    /// The last failure's message, truncated — the audit-webhook health column's precedent.
    /// </summary>
    /// <remarks>
    /// <b>A message, never a payload.</b> ADR 0626's redaction rule binds here exactly as it does in the log:
    /// the exception's own sentence is safe, what the provider sent back is not, and a response body is the
    /// one thing most likely to carry a credential or a licensed artefact. Truncated so a verbose provider
    /// cannot turn an administrator's status line into a wall of text.
    /// </remarks>
    public string LastError { get; set; } = string.Empty;

    /// <summary>
    /// When the tenant's administrators were told, or null while the episode is below the threshold. Set once
    /// per episode: the row is deleted on the next success, so a genuinely new episode notifies again, while
    /// a source that stays broken does not re-notify every time somebody opens the folder.
    /// </summary>
    public DateTimeOffset? NotifiedAt { get; set; }
}
