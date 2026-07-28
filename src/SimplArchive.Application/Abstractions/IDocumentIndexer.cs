namespace SimplArchive.Application.Abstractions;

// Keeps the search index in sync with a document — see ADR "Search / full-text indexing model" (0011) and
// "OpenSearch full-text slice 1". Called by the background SearchIndexWorker draining the outbox (ADR "Async
// indexing"); the implementation swallows its own errors so a search-engine hiccup never fails anything. A
// no-op implementation is registered when OpenSearch isn't configured.
public interface IDocumentIndexer
{
    // Re-indexes the document from its current state (name + index-field values + latest confirmed version's
    // extracted content), or removes it if it's gone/soft-deleted. Returns true on success (indexed or
    // confirmed-removed), false if it failed (e.g. OpenSearch unreachable) — the worker retries on false.
    Task<bool> SyncAsync(Guid documentId, CancellationToken cancellationToken = default);

    // Removes the document from the index (used for delete-cascade).
    Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default);
}
