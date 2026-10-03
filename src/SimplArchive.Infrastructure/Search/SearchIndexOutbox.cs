namespace SimplArchive.Infrastructure.Search;

// A durable "(re)sync this document into the search index" event (ADR "Async indexing", 0011). The
// controllers enqueue one after a document mutation commits; SearchIndexWorker drains it in the background,
// off the request path. Deliberately NOT ITenantScoped — the worker processes every tenant's rows and sets
// the tenant context per row (TenantId below); and NOT FK'd to Document, since a delete's row must survive
// the document's removal so the worker can process the delete-from-index.
public sealed class SearchIndexOutbox
{
    public Guid Id { get; set; }

    // The document to (re)sync — the worker calls IDocumentIndexer.SyncAsync, which indexes it from current
    // state or removes it if it's gone/soft-deleted.
    public Guid DocumentId { get; set; }

    // The document's tenant, so the worker can set the tenant context before the (tenant-filtered) content
    // queries run. Guid.Empty if no tenant was in scope when enqueued (a background/system write).
    public Guid TenantId { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }

    // Null = pending. Set by a worker's CLAIM (one compare-and-swap on `ClaimedAt IS NULL`) before it syncs the
    // document, so the second app instance (ADR 0808) skips the row instead of colliding on its delete (#1587,
    // ADR 0836). Also the claim's LIFETIME: a claim older than the worker's lease belonged to a process that died
    // between the sync and the delete, and is put back.
    public DateTimeOffset? ClaimedAt { get; set; }
}
