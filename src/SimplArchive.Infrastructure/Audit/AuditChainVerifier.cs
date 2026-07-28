using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Audit;

// Verifies the current tenant's audit hash chain (ADRs "Audit trail hash chain" and "... retention and
// purge"). Reads the tenant's events in Sequence order (the DbContext tenant filter scopes to the caller's
// tenant) and recomputes each link with the same AuditEventHasher the recorder used: a mismatch means a field
// was edited; a Sequence gap means a row was deleted or reordered. Reports the first break. The walk starts
// from the tenant's retained-window checkpoint (Tenant.AuditChainStart…), not genesis, so a legitimate purge
// of the oldest prefix isn't flagged as tampering. Registered scoped in AddInfrastructure.
public sealed class AuditChainVerifier : IAuditChainVerifier
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public AuditChainVerifier(SimplArchiveDbContext dbContext, ICurrentTenantAccessor currentTenantAccessor)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
    }

    public async Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        // Start from the retained-window checkpoint: after a purge of the oldest prefix, verification resumes
        // from the first retained Sequence using the last-purged event's hash as the "previous".
        var checkpoint = _currentTenantAccessor.TenantId is { } tenantId
            ? await _dbContext.Tenants
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.AuditChainStartSequence, t.AuditChainStartPreviousHash })
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var previousHash = checkpoint?.AuditChainStartPreviousHash ?? AuditEventHasher.Genesis;
        var expectedSequence = checkpoint?.AuditChainStartSequence ?? 0;
        var checkedCount = 0;

        // Stream in Sequence order; tenant-filtered to the caller's tenant.
        var events = _dbContext.AuditEvents
            .OrderBy(e => e.Sequence)
            .AsAsyncEnumerable();

        await foreach (var e in events.WithCancellation(cancellationToken))
        {
            // A gap or reorder — a row was deleted or moved.
            if (e.Sequence != expectedSequence)
            {
                return new AuditChainVerification(false, checkedCount, e.Sequence);
            }

            if (AuditEventHasher.ComputeHash(previousHash, e) != e.Hash)
            {
                return new AuditChainVerification(false, checkedCount, e.Sequence);
            }

            previousHash = e.Hash;
            expectedSequence++;
            checkedCount++;
        }

        return new AuditChainVerification(true, checkedCount, null);
    }
}
