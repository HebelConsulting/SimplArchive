namespace SimplArchive.Application.Abstractions;

// Per-tenant storage quota (ADR "Per-tenant storage quota"). App-level enforcement (portable across S3/SeaweedFS,
// not a native bucket quota): a maintained Tenant.StorageUsedBytes counter is checked against the tenant's
// StorageQuotaBytes limit before a new blob is stored, and adjusted as confirmed version blobs are added/purged.
public interface IStorageQuotaService
{
    // True if the tenant may store additionalBytes more — i.e. it has no quota, or used + additionalBytes is
    // within it. The check reads the maintained counter; concurrent uploads can each pass and slightly overshoot
    // (a coarse guard by design). Called at a user upload entry point before the blob is committed to the archive.
    Task<bool> CanStoreAsync(Guid tenantId, long additionalBytes, CancellationToken cancellationToken = default);

    // Adjusts the tenant's maintained used-storage counter by deltaBytes (negative on purge). Atomic at the DB
    // level (UPDATE … SET StorageUsedBytes = StorageUsedBytes + delta), so concurrent adjustments don't race.
    Task AdjustUsageAsync(Guid tenantId, long deltaBytes, CancellationToken cancellationToken = default);
}
