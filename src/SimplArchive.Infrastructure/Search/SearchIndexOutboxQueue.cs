using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Search;

// Writes a SearchIndexOutbox row (ADR "Async indexing", 0011). Enqueue is a quick insert on the request's
// DbContext committed right after the caller's mutation — decoupling the write path from Tika/OpenSearch. It
// commits in its own transaction (not the mutation's), so the rare crash between the two commits could drop
// an event; the reindex-all backfill (ADR 0254) heals that. Once committed, the worker processes it reliably
// (at-least-once, with retry) even across a restart.
public sealed class SearchIndexOutboxQueue : IDocumentIndexQueue
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public SearchIndexOutboxQueue(SimplArchiveDbContext dbContext, ICurrentTenantAccessor tenantAccessor)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
    }

    public Task EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        EnqueueManyAsync([documentId], cancellationToken);

    public async Task EnqueueManyAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return;
        }

        var tenantId = _tenantAccessor.TenantId ?? Guid.Empty;
        var now = DateTimeOffset.UtcNow;
        foreach (var documentId in documentIds)
        {
            _dbContext.SearchIndexOutbox.Add(new SearchIndexOutbox
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                TenantId = tenantId,
                EnqueuedAt = now,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

// Registered when OpenSearch isn't configured — nothing consumes the queue, so enqueueing is a no-op.
public sealed class NullDocumentIndexQueue : IDocumentIndexQueue
{
    public Task EnqueueAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task EnqueueManyAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
