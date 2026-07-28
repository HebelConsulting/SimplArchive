using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Conversion;

// Writes a SearchablePdfOutbox row (ADR "Searchable PDF successor for TIFFs") — a quick insert on the
// request's DbContext after the version is confirmed, committed in its own transaction (a crash in the gap
// just means no successor is generated; the original TIFF is untouched). Mirrors SearchIndexOutboxQueue.
public sealed class SearchablePdfOutboxQueue : ISearchablePdfQueue
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public SearchablePdfOutboxQueue(SimplArchiveDbContext dbContext, ICurrentTenantAccessor tenantAccessor)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
    }

    public async Task EnqueueAsync(Guid documentId, Guid sourceVersionId, CancellationToken cancellationToken = default)
    {
        _dbContext.SearchablePdfOutbox.Add(new SearchablePdfOutbox
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantAccessor.TenantId ?? Guid.Empty,
            DocumentId = documentId,
            SourceVersionId = sourceVersionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Attempts = 0,
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> EnqueueManyAsync(IReadOnlyCollection<SearchablePdfJob> jobs, CancellationToken cancellationToken = default)
    {
        if (jobs.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var job in jobs)
        {
            _dbContext.SearchablePdfOutbox.Add(new SearchablePdfOutbox
            {
                Id = Guid.NewGuid(),
                TenantId = job.TenantId,
                DocumentId = job.DocumentId,
                SourceVersionId = job.SourceVersionId,
                CreatedAt = now,
                Attempts = 0,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return jobs.Count;
    }
}

// Registered when Ocr:Url isn't configured — nothing consumes the queue, so enqueueing is a no-op.
public sealed class NullSearchablePdfQueue : ISearchablePdfQueue
{
    public Task EnqueueAsync(Guid documentId, Guid sourceVersionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> EnqueueManyAsync(IReadOnlyCollection<SearchablePdfJob> jobs, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
