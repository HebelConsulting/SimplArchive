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
}
