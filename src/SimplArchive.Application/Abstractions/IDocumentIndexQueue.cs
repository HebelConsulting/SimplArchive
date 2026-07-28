namespace SimplArchive.Application.Abstractions;

// Enqueues a document for asynchronous (re)indexing (ADR "Async indexing", 0011). The controllers call this
// after a mutating operation commits — a fast, durable insert — instead of doing the OpenSearch/Tika work in
// the request. A background worker (SearchIndexWorker) drains the queue and calls IDocumentIndexer. A no-op
// implementation is registered when OpenSearch isn't configured. Covers removal too: the worker's SyncAsync
// removes a document that has since been deleted/soft-deleted, so a delete just enqueues like any other change.
public interface IDocumentIndexQueue
{
    Task EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default);

    // Enqueues a set of documents in one commit — used for the subtree fan-out when a document's ACL changes
    // (ADR "Indexed ACL in search"), where every descendant's indexed visibility may have changed too.
    Task EnqueueManyAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);
}
