using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Audit;

// Verifies the current tenant's sealed WORM audit segments (ADR "Audit WORM segment verify"). Reads the immutable
// NDJSON segments from object storage in Sequence order, checks they're contiguous from 0 up to the archived
// checkpoint, and confirms each sealed event's hash matches the DB event at that Sequence. Because the segments
// are object-lock-immutable, a hash mismatch means the DB was tampered (even a full re-chain, which the DB chain
// check can't detect). Tenant-scoped via the DbContext filter. Registered scoped in AddInfrastructure.
public sealed class AuditWormVerifier : IAuditWormVerifier
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorage;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public AuditWormVerifier(SimplArchiveDbContext dbContext, IObjectStorageClient objectStorage, ICurrentTenantAccessor currentTenantAccessor)
    {
        _dbContext = dbContext;
        _objectStorage = objectStorage;
        _currentTenantAccessor = currentTenantAccessor;
    }

    public async Task<AuditWormVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTenantAccessor.TenantId is not { } tenantId)
        {
            return new AuditWormVerification(true, 0, 0, null, null);
        }

        var archivedThrough = await _dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.AuditWormArchivedThrough)
            .SingleOrDefaultAsync(cancellationToken);

        // The DB's current hash per Sequence (tenant-filtered). A sealed Sequence the DB no longer has (purged
        // below the retained window) can't be compared — the segment stands alone as immutable evidence.
        var dbHashes = await _dbContext.AuditEvents
            .Select(e => new { e.Sequence, e.Hash })
            .ToDictionaryAsync(e => e.Sequence, e => e.Hash, cancellationToken);

        var prefix = $"tenants/{tenantId}/{AuditWormArchiver.Prefix}/";
        var segments = (await _objectStorage.ListObjectsAsync(prefix, cancellationToken))
            .OrderBy(o => o.Key, StringComparer.Ordinal) // the {from:D20}-{to:D20} names sort in Sequence order
            .ToList();

        long expected = 0;
        var checkedCount = 0;

        foreach (var segment in segments)
        {
            string content;
            await using (var stream = await _objectStorage.GetObjectAsync(segment.Key, cancellationToken))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                content = await reader.ReadToEndAsync(cancellationToken);
            }

            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var doc = JsonDocument.Parse(line);
                var sequence = doc.RootElement.GetProperty("sequence").GetInt64();
                var hash = doc.RootElement.GetProperty("hash").GetString();

                // Contiguity: sealed events must run 0, 1, 2, … with no gap/reorder.
                if (sequence != expected)
                {
                    return new AuditWormVerification(false, segments.Count, checkedCount, sequence, "segment-gap");
                }

                // Tamper detection: the immutable sealed hash must equal the DB's current hash for that event.
                if (dbHashes.TryGetValue(sequence, out var dbHash) && dbHash != hash)
                {
                    return new AuditWormVerification(false, segments.Count, checkedCount, sequence, "db-mismatch");
                }

                expected++;
                checkedCount++;
            }
        }

        // The sealed segments must reach the archived checkpoint — otherwise a trailing segment is missing.
        if (archivedThrough >= 0 && expected - 1 != archivedThrough)
        {
            return new AuditWormVerification(false, segments.Count, checkedCount, expected, "missing-segment");
        }

        return new AuditWormVerification(true, segments.Count, checkedCount, null, null);
    }
}
