namespace SimplArchive.Application.Abstractions;

// Enqueues a "generate a searchable-PDF successor version for this TIFF version" job, drained off the request
// path by a background worker (ADR "Searchable PDF successor for TIFFs"). DocumentFinalizer enqueues one after
// any TIFF version is confirmed. No-op when no OCR converter is configured.
public interface ISearchablePdfQueue
{
    // Enqueue one job, taking the tenant from the current request context (the upload / set-languages paths).
    Task EnqueueAsync(Guid documentId, Guid sourceVersionId, CancellationToken cancellationToken = default);

    // Enqueue many jobs, each with an explicit tenant — the backfill (ADR "Backfill searchable PDFs for
    // existing TIFFs") runs for a PlatformAdministrator with no tenant context, so it can't read the tenant
    // from the request. Returns the number actually enqueued (0 when no OCR converter is configured).
    Task<int> EnqueueManyAsync(IReadOnlyCollection<SearchablePdfJob> jobs, CancellationToken cancellationToken = default);
}

// A searchable-PDF conversion job with its tenant made explicit — for the cross-tenant backfill.
public sealed record SearchablePdfJob(Guid TenantId, Guid DocumentId, Guid SourceVersionId);
