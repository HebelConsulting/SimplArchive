using SimplArchive.Application.Abstractions;

namespace SimplArchive.IntegrationTests;

// No-op queue doubles for the integration tests that construct RepositoryImporter directly. The importer takes
// the index + searchable-PDF queues (it enqueues async (re)indexing and TIFF→searchable-PDF conversion after an
// import commits), but these SQLite tests assert the imported rows, not the async indexing/OCR fan-out, so the
// queues are stubbed to do nothing.
internal sealed class NoOpDocumentIndexQueue : IDocumentIndexQueue
{
    public static readonly NoOpDocumentIndexQueue Instance = new();
    public Task EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task EnqueueManyAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NoOpSearchablePdfQueue : ISearchablePdfQueue
{
    public static readonly NoOpSearchablePdfQueue Instance = new();
    public Task EnqueueAsync(Guid documentId, Guid sourceVersionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> EnqueueManyAsync(IReadOnlyCollection<SearchablePdfJob> jobs, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
