namespace SimplArchive.Application.Abstractions;

// Seals a tenant's newly-committed audit events into an immutable WORM segment in object storage (ADR
// "Audit-log WORM"). Writes the contiguous run of events past the tenant's AuditWormArchivedThrough checkpoint
// as an NDJSON object, locks it with S3 Object Lock (retention = the tenant's audit-retention window, in its
// WormLockMode), and advances the checkpoint. Idempotent; a no-op when there's nothing new. Run by the hosted
// AuditWormWorker; also callable directly (tests). Returns the number of events sealed.
public interface IAuditWormArchiver
{
    Task<int> ArchiveAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
