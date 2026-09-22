using SimplArchive.Domain.Audit;

namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Records a security-sensitive action to the append-only audit log (ADR "Audit trail (first slice)").
/// Called at the mutation site after the action itself has succeeded; resolves the current actor
/// (User / ServiceAccount / PlatformAdministrator) and their name snapshot, and persists an
/// <c>AuditEvent</c> in its own commit. Best-effort ordering — a crash between the action's commit and the
/// audit commit could drop the event (acceptable for this slice, same class of gap as the search outbox).
/// </summary>
public interface IAuditRecorder
{
    /// <param name="action">A stable action code — see <c>AuditActions</c> (e.g. "Document.Deleted").</param>
    /// <param name="tenantId">The tenant the event belongs to; defaults to the current tenant. Pass explicitly
    /// when the actor's current tenant differs from the target's — e.g. a PlatformAdministrator creating a
    /// tenant records against the new tenant.</param>
    Task RecordAsync(
        string action,
        string? targetType = null,
        Guid? targetId = null,
        string? targetName = null,
        string? details = null,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an event with the actor supplied explicitly — REQUIRED on every path with no ambient
    /// principal: background workers and sweeps (System + a descriptive subsystem name, the
    /// RetentionService idiom), startup seeding, and pre-sign-in edges like the login POST.
    /// <see cref="RecordAsync"/> cannot write there — it warns and drops the event (#1312), which is how
    /// the EmailAbandoned events were silently never written in production.
    /// </summary>
    Task RecordForActorAsync(
        AuditActorType actorType,
        Guid actorId,
        string actorName,
        Guid tenantId,
        string action,
        string? targetType = null,
        Guid? targetId = null,
        string? targetName = null,
        string? details = null,
        CancellationToken cancellationToken = default);
}
