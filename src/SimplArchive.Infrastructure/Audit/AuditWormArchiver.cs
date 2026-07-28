using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;

namespace SimplArchive.Infrastructure.Audit;

// Seals a tenant's audit events into immutable WORM segments (ADR "Audit-log WORM"). The DB stays the queryable
// copy + hash chain; this writes a tamper-proof archive to object storage so the log can't be altered/deleted
// even by a DB admin. Only a CONTIGUOUS run past the checkpoint is sealed (stopping at the first Sequence gap),
// so every segment is a gap-free range that a downstream verifier can chain-check. Scoped.
public sealed class AuditWormArchiver : IAuditWormArchiver
{
    // The far-future retain-until used when a tenant keeps audit events forever (AuditRetentionDays = 0).
    private static readonly TimeSpan KeepForeverLock = TimeSpan.FromDays(365 * 100);
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];
    private static readonly JsonSerializerOptions LineJsonOptions = new(JsonSerializerDefaults.Web);

    public const string Prefix = "audit-worm";

    private readonly Persistence.SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorage;
    private readonly ILogger<AuditWormArchiver> _logger;

    public AuditWormArchiver(Persistence.SimplArchiveDbContext dbContext, IObjectStorageClient objectStorage, ILogger<AuditWormArchiver> logger)
    {
        _dbContext = dbContext;
        _objectStorage = objectStorage;
        _logger = logger;
    }

    public async Task<int> ArchiveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return 0;
        }

        var events = _dbContext.AuditEvents.IgnoreQueryFilters(TenantFilterOnly).Where(e => e.TenantId == tenantId);

        var pending = await events
            .Where(e => e.Sequence > tenant.AuditWormArchivedThrough)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        // Seal only the gap-free run from checkpoint+1 (a higher Sequence can commit before a lower one under
        // concurrency); the tail is picked up next sweep once the gap fills.
        var expected = tenant.AuditWormArchivedThrough + 1;
        var contiguous = new List<AuditEvent>();
        foreach (var e in pending)
        {
            if (e.Sequence != expected)
            {
                break;
            }

            contiguous.Add(e);
            expected++;
        }

        if (contiguous.Count == 0)
        {
            return 0;
        }

        var from = contiguous[0].Sequence;
        var to = contiguous[^1].Sequence;

        var builder = new StringBuilder();
        foreach (var e in contiguous)
        {
            builder.Append(JsonSerializer.Serialize(ToLine(e), LineJsonOptions)).Append('\n');
        }

        var key = $"tenants/{tenantId}/{Prefix}/{from:D20}-{to:D20}.ndjson";
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        await _objectStorage.PutObjectAsync(key, new MemoryStream(bytes), "application/x-ndjson", cancellationToken);

        // Lock the segment with Object Lock — retention = the tenant's audit-retention window (0 = keep forever),
        // in the tenant's configured WORM mode. Best-effort: a non-object-lock bucket still gets the segment
        // written (just not storage-immutable), logged.
        var retainUntil = tenant.AuditRetentionDays > 0
            ? DateTimeOffset.UtcNow.AddDays(tenant.AuditRetentionDays)
            : DateTimeOffset.UtcNow.Add(KeepForeverLock);
        try
        {
            await _objectStorage.SetRetentionAsync(key, retainUntil, tenant.WormLockMode, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Audit WORM: wrote segment {Key} but could not apply Object Lock (non-object-lock bucket?).", key);
        }

        tenant.AuditWormArchivedThrough = to;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return contiguous.Count;
    }

    // The archived line shape mirrors the NDJSON export (ADR "Audit trail export") — Sequence + Hash carry the
    // tamper-evidence chain so a segment is independently verifiable.
    private static object ToLine(AuditEvent e) => new
    {
        sequence = e.Sequence,
        hash = e.Hash,
        timestamp = e.Timestamp,
        actorType = e.ActorType.ToString(),
        actorId = e.ActorId,
        actorName = e.ActorName,
        action = e.Action,
        targetType = e.TargetType,
        targetId = e.TargetId,
        targetName = e.TargetName,
        details = e.Details,
    };
}
