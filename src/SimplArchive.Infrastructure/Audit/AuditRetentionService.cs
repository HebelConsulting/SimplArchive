using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Audit;

// Purges a tenant's aged audit events while keeping the hash chain verifiable (ADR "Audit trail retention and
// purge"). Deletes the oldest contiguous Sequence prefix whose events are past the retention window — but never
// the chain tip (so the recorder's MAX(Sequence) high-water is preserved and Sequences are never reused) — and
// advances the tenant's retained-window checkpoint (AuditChainStart…) to the last-purged event's hash, in one
// transaction so a crash can't leave events deleted without the checkpoint moved. Registered scoped.
public sealed class AuditRetentionService : IAuditRetentionService
{
    private const int MaxPurgePerSweep = 5000;
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;

    public AuditRetentionService(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> PurgeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null || tenant.AuditRetentionDays <= 0)
        {
            return 0; // unknown tenant, or retention disabled (keep forever)
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-tenant.AuditRetentionDays);

        // Explicit tenant filter (the worker may have no/other tenant context; the endpoint passes the caller's).
        var events = _dbContext.AuditEvents.IgnoreQueryFilters(TenantFilterOnly).Where(e => e.TenantId == tenantId);

        if (await events.MaxAsync(e => (long?)e.Sequence, cancellationToken) is not { } tipSequence)
        {
            return 0; // no events
        }

        // Load a bounded, Sequence-ordered batch of the retained non-tip events and find the purge boundary
        // client-side — the Timestamp cutoff is compared in memory because SQLite (the test provider) can't
        // translate a DateTimeOffset comparison in SQL. Sequence order == append/time order, so events past the
        // window form a contiguous prefix and the scan stops at the first still-in-window event. The batch cap
        // bounds each sweep (the hourly worker catches up over successive sweeps); the tip is always excluded so
        // the chain keeps ≥1 event and the recorder's Sequence high-water survives.
        var candidates = await events
            .Where(e => e.Sequence >= tenant.AuditChainStartSequence && e.Sequence < tipSequence)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Timestamp })
            .Take(MaxPurgePerSweep)
            .ToListAsync(cancellationToken);

        long? boundary = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Timestamp >= cutoff)
            {
                break;
            }

            boundary = candidate.Sequence;
        }

        if (boundary is not { } boundarySequence)
        {
            return 0; // nothing old enough
        }

        // The boundary event's hash becomes the "previous hash" the first retained event chains from — capture
        // it before deleting the prefix.
        var boundaryHash = await events
            .Where(e => e.Sequence == boundarySequence)
            .Select(e => e.Hash)
            .SingleAsync(cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var purged = await events
            .Where(e => e.Sequence >= tenant.AuditChainStartSequence && e.Sequence <= boundarySequence)
            .ExecuteDeleteAsync(cancellationToken);

        tenant.AuditChainStartSequence = boundarySequence + 1;
        tenant.AuditChainStartPreviousHash = boundaryHash;
        tenant.AuditLastPurgedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return purged;
    }
}
