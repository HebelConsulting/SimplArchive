using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Search;

// Drains the SearchIndexOutbox in the background (ADR "Async indexing", 0011), off the request path.
// Registered only when OpenSearch is configured. Oldest-first, deduped by document (current state indexed
// once), setting the tenant context per row so the indexer's tenant-filtered queries resolve. A row is
// deleted only when its sync succeeds — so an OpenSearch outage retries rather than losing the event
// (at-least-once). Single-instance: multi-pod claim-locking (SKIP LOCKED) is out of scope; SyncAsync is
// idempotent, so a rare double-process is harmless.
public sealed class SearchIndexWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchReindexState _reindexState;
    private readonly ILogger<SearchIndexWorker> _logger;

    public SearchIndexWorker(IServiceScopeFactory scopeFactory, SearchReindexState reindexState, ILogger<SearchIndexWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _reindexState = reindexState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (poll interval {Interval}).", nameof(SearchIndexWorker), PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Made progress → loop immediately to drain the backlog; otherwise (idle or all-failing) back off.
                if (!await DrainOnceAsync(stoppingToken))
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Search index worker loop failed.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>One drain pass; public because it IS the unit the #661 race fix is tested at.</summary>
    public async Task<bool> DrainOnceAsync(CancellationToken cancellationToken)
    {
        // PAUSED while a rebuild runs (#661, the data-losing race): this worker writes through the alias,
        // which during a rebuild points at the index the swap is about to DELETE. A row drained in that
        // window succeeded, so it was removed — and then the swap took the document with the old index,
        // leaving nothing anywhere that says so. Holding the rows instead means they drain into the NEW
        // index right after the swap. The boundary is safe without further ceremony: a batch already in
        // flight when the flag went up holds only rows committed before the rebuild's snapshot read, and
        // those documents are in the snapshot.
        //
        // Deliberately unconditional — the first build pauses too. Its alias exists from the start and the
        // backfill covers everything committed before its snapshot; rows for anything later simply wait the
        // few extra seconds. One rule beats two.
        if (_reindexState.IsRunning)
        {
            _logger.LogDebug("A search-index rebuild is running; holding the outbox until it swaps.");
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>();
        var indexer = scope.ServiceProvider.GetRequiredService<IDocumentIndexer>();

        // Oldest-first, ordered CLIENT-SIDE over a keys-only projection — SQLite cannot translate a
        // DateTimeOffset ORDER BY (the RepositoryExporter precedent), and this worker's drain pass is now
        // exercised against SQLite by the #661 pause test. The projection is two columns over a table that
        // is empty in the steady state; the second query fetches only the chosen batch.
        var keys = (await dbContext.SearchIndexOutbox
                .Select(o => new { o.Id, o.EnqueuedAt })
                .ToListAsync(cancellationToken))
            .OrderBy(o => o.EnqueuedAt)
            .ThenBy(o => o.Id)
            .Take(BatchSize)
            .Select(o => o.Id)
            .ToList();

        var batch = await dbContext.SearchIndexOutbox
            .Where(o => keys.Contains(o.Id))
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            _logger.LogDebug("Search index outbox is empty; nothing to drain.");
            return false;
        }

        var deletedAny = false;
        var indexedCount = 0;
        foreach (var group in batch.GroupBy(o => o.DocumentId))
        {
            var rows = group.ToList();
            var tenantId = rows[0].TenantId;
            tenantAccessor.TenantId = tenantId == Guid.Empty ? null : tenantId;

            _logger.LogDebug("Syncing document {DocumentId} in tenant {TenantId} to the search index.", group.Key, tenantId);
            if (await indexer.SyncAsync(group.Key, cancellationToken))
            {
                dbContext.SearchIndexOutbox.RemoveRange(rows);
                deletedAny = true;
                indexedCount++;
            }
            // else: leave the rows for the next poll (retry — e.g. OpenSearch briefly unreachable)
        }

        if (deletedAny)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Synced {Count} document(s) to the search index.", indexedCount);
        }

        return deletedAny;
    }
}
