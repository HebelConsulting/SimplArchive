namespace SimplArchive.Application.Abstractions;

// Applies WORM (write-once-read-many) immutability to a document's confirmed version blobs via S3 Object Lock
// (ADR "WORM / immutable document versions"). ReconcileAsync computes the desired lock state for each blob from
// the document's current retention policy (mask RetentionYears → an Object Lock retention until the disposition
// date, in the tenant's WormLockMode) and legal-hold state (a direct active legal hold → an object legal hold),
// and applies it. Idempotent; best-effort (a storage failure is logged, not thrown, so it never breaks the
// triggering mutation). Called at the trigger sites: version finalize, mask assign/change, document-date change,
// and legal-hold add/remove/release.
public interface IWormLockService
{
    Task ReconcileAsync(Guid documentId, CancellationToken cancellationToken = default);
}
