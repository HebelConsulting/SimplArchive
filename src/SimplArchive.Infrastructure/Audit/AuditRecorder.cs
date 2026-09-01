using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Audit;

// See ADR "Audit trail (first slice)". Resolves the current actor + a name snapshot and appends an
// AuditEvent. Registered scoped in AddInfrastructure. If no actor or tenant can be resolved (shouldn't
// happen on an authorized mutation path), the call is a no-op rather than throwing — auditing must never
// break the action it records.
public class AuditRecorder : IAuditRecorder
{
    private const int MaxAppendAttempts = 8;
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentPlatformAdministratorAccessor _currentPlatformAdministratorAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentImpersonationAccessor _currentImpersonationAccessor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditRecorder> _logger;

    public AuditRecorder(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentPlatformAdministratorAccessor currentPlatformAdministratorAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentImpersonationAccessor currentImpersonationAccessor,
        [FromKeyedServices("demo-clock")] TimeProvider timeProvider,
        ILogger<AuditRecorder> logger)
    {
        _logger = logger;
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentPlatformAdministratorAccessor = currentPlatformAdministratorAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _currentImpersonationAccessor = currentImpersonationAccessor;
        _timeProvider = timeProvider;
    }

    public async Task RecordAsync(
        string action,
        string? targetType = null,
        Guid? targetId = null,
        string? targetName = null,
        string? details = null,
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var (actorType, actorId, actorName) = await ResolveActorAsync(cancellationToken);

        if (actorId is not { } resolvedActorId || (tenantId ?? _currentTenantAccessor.TenantId) is not { } effectiveTenant)
        {
            return;
        }

        await AppendAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = effectiveTenant,
            Timestamp = AuditEventHasher.TruncateToMicroseconds(_timeProvider.GetUtcNow()),
            ActorType = actorType,
            ActorId = resolvedActorId,
            ActorName = actorName ?? "(unknown)",
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            Details = details,
            Hash = string.Empty, // set by AppendAsync
        }, effectiveTenant, cancellationToken);
    }

    public async Task RecordForActorAsync(
        AuditActorType actorType,
        Guid actorId,
        string actorName,
        Guid tenantId,
        string action,
        string? targetType = null,
        Guid? targetId = null,
        string? targetName = null,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        await AppendAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Timestamp = AuditEventHasher.TruncateToMicroseconds(_timeProvider.GetUtcNow()),
            ActorType = actorType,
            ActorId = actorId,
            ActorName = actorName,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            Details = details,
            Hash = string.Empty, // set by AppendAsync
        }, tenantId, cancellationToken);
    }

    // Appends the event as the next link in its tenant's hash chain (ADR "Audit trail hash chain"): reads the
    // chain tip (its Sequence + Hash) for that tenant — ignoring the tenant query filter, since a record may
    // target a tenant other than the current request context (a platform-admin tenant-create, a login) — then
    // sets Sequence = tip+1 and Hash = SHA-256(tip.Hash + canonical). A concurrent same-tenant append that
    // races to the same Sequence loses the unique (TenantId, Sequence) index and is retried.
    private async Task AppendAsync(AuditEvent auditEvent, Guid tenantId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var tip = await _dbContext.AuditEvents
                .IgnoreQueryFilters(TenantFilterOnly)
                .Where(e => e.TenantId == tenantId)
                .OrderByDescending(e => e.Sequence)
                .Select(e => new { e.Sequence, e.Hash })
                .FirstOrDefaultAsync(cancellationToken);

            auditEvent.Sequence = tip is null ? 0 : tip.Sequence + 1;
            auditEvent.Hash = AuditEventHasher.ComputeHash(tip?.Hash ?? AuditEventHasher.Genesis, auditEvent);

            _dbContext.AuditEvents.Add(auditEvent);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < MaxAppendAttempts)
            {
                // Lost the race for this Sequence — detach and recompute against the new tip.
                //
                // DEBUG, not Warning, though CLAUDE.md lists "a transient failure that will retry" under
                // Warning: this is not a failure retried, it is the arbitration mechanism working. Two
                // same-tenant appends reached the same Sequence and the loser recomputes; nothing degraded and
                // no admin has anything to act on. Logging it at Warning would recreate one level down exactly
                // the problem issue #759 is about — a level an operator learns to ignore on this service.
                _logger.LogDebug(
                    "Audit append for tenant {TenantId} lost sequence {Sequence} to a concurrent append; retrying ({Attempt} of {MaxAttempts})",
                    tenantId, auditEvent.Sequence, attempt, MaxAppendAttempts);

                _dbContext.Entry(auditEvent).State = EntityState.Detached;
            }
            catch (DbUpdateException ex)
            {
                // Attempts exhausted. THIS is the one an admin must investigate, and it is the reason the
                // EF-generated Error lines for the ordinary contention can be filtered out at all (see
                // SerilogConfiguration.IsAuditChainContention): the case that matters keeps a line of its own,
                // which names the audit chain rather than reporting an anonymous duplicate key.
                _logger.LogError(
                    ex,
                    "Audit append for tenant {TenantId} failed after {MaxAttempts} attempts at sequence {Sequence}; the {Action} event on {TargetType} {TargetId} was NOT recorded",
                    tenantId, MaxAppendAttempts, auditEvent.Sequence, auditEvent.Action, auditEvent.TargetType, auditEvent.TargetId);

                throw;
            }
        }
    }

    // Name lookups IgnoreQueryFilters — a PlatformAdministrator has no tenant, and even a User/ServiceAccount
    // event may be recorded against a tenant other than the one the filter is set to.
    private async Task<(AuditActorType Type, Guid? Id, string? Name)> ResolveActorAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            var name = await _dbContext.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken);

            // During impersonation (ADR "User impersonation") the actor is the target user; annotate the name
            // with the acting admin so every impersonated action stays attributable.
            if (_currentImpersonationAccessor.ImpersonatorUserId is { } impersonatorId)
            {
                var impersonator = await _dbContext.Users.IgnoreQueryFilters()
                    .Where(u => u.Id == impersonatorId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken);
                name = $"{name} (impersonated by {impersonator})";
            }

            return (AuditActorType.User, userId, name);
        }

        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            var name = await _dbContext.ServiceAccounts.IgnoreQueryFilters()
                .Where(s => s.Id == serviceAccountId).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken);
            return (AuditActorType.ServiceAccount, serviceAccountId, name);
        }

        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is { } platformAdministratorId)
        {
            var name = await _dbContext.PlatformAdministrators
                .Where(p => p.Id == platformAdministratorId).Select(p => p.Name).SingleOrDefaultAsync(cancellationToken);
            return (AuditActorType.PlatformAdministrator, platformAdministratorId, name);
        }

        return (AuditActorType.User, null, null);
    }
}
