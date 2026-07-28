namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Resolves a user's <em>effective</em> tenant-wide system rights — their own rights unioned with the
/// rights of every group they effectively belong to (direct membership plus descendant groups, the same
/// "membership flows down" expansion the ACL layer uses). See ADR "Enforce group system rights for
/// members". Every place that gates on a <c>User</c>'s system right (the management endpoints, the
/// searchable-PDF backfill, <c>whoami</c>, and the rights-assignment escalation cap) resolves through this
/// rather than reading the <c>User</c> row directly, so a right held only via a group takes effect.
/// </summary>
public interface IUserSystemRightsResolver
{
    Task<SystemRightsSet> GetEffectiveSystemRightsAsync(Guid userId, CancellationToken cancellationToken = default);
}
