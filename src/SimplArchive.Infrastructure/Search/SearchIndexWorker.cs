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
// (at-least-once).
//
// It CLAIMS each document's rows before syncing (#1587, ADR 0836). This used to say "single-instance", and that
// stopped being true with ADR 0808: both app instances run this worker, and the old read-then-commit-the-batch
// shape made one contended row discard the deletes for every other document in the batch — across tenants —
// behind a warning that named neither. Now each document is claimed (a compare-and-swap on `ClaimedAt IS NULL`),
// synced and completed on its own, so contention costs the loser one skipped document and nothing else.
public sealed class SearchIndexWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int BatchSize = 100;

    // How long a claim may stand before it is taken to belong to a process that died between the sync and the
    // delete. Far above one document's sync; reclaiming re-syncs an idempotent document, so erring long only
    // delays a crashed row by minutes.
    public static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(5);

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

        await ReclaimAbandonedClaimsAsync(dbContext, cancellationToken);

        // Unclaimed rows, oldest-first, ordered CLIENT-SIDE over a keys-only projection — SQLite cannot translate a
        // DateTimeOffset ORDER BY (the RepositoryExporter precedent), and this drain pass is exercised against
        // SQLite. AsNoTracking is load-bearing (ADR 0836): the claims below are ExecuteUpdates the ChangeTracker
        // never sees, and nothing here may be saved through it.
        var batch = (await dbContext.SearchIndexOutbox.AsNoTracking()
                .Where(o => o.ClaimedAt == null)
                .Select(o => new { o.Id, o.DocumentId, o.TenantId, o.EnqueuedAt })
                .ToListAsync(cancellationToken))
            .OrderBy(o => o.EnqueuedAt)
            .ThenBy(o => o.Id)
            .Take(BatchSize)
            .ToList();

        if (batch.Count == 0)
        {
            _logger.LogDebug("Search index outbox is empty; nothing to drain.");
            return false;
        }

        var indexedCount = 0;
        foreach (var group in batch.GroupBy(o => o.DocumentId))
        {
            var ids = group.Select(o => o.Id).ToList();
            var tenantId = group.First().TenantId;

            // THE CLAIM: `ClaimedAt IS NULL` is both what the read selected and what this swaps, so the other
            // instance's sweep that got here first leaves nothing to win and this one moves on.
            var now = DateTimeOffset.UtcNow;
            var won = await dbContext.SearchIndexOutbox
                .Where(o => ids.Contains(o.Id) && o.ClaimedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.ClaimedAt, now), cancellationToken);
            if (won == 0)
            {
                _logger.LogDebug("Document {DocumentId} is being indexed by another instance; skipping.", group.Key);
                continue;
            }

            tenantAccessor.TenantId = tenantId == Guid.Empty ? null : tenantId;
            _logger.LogDebug("Syncing document {DocumentId} in tenant {TenantId} to the search index.", group.Key, tenantId);

            bool synced;
            try
            {
                synced = await indexer.SyncAsync(group.Key, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Named, and confined to this document: the old shape's one failure took the whole batch with it
                // and logged "loop failed" about nothing in particular.
                _logger.LogWarning(e, "Indexing document {DocumentId} in tenant {TenantId} failed; it will be retried.",
                    group.Key, tenantId);
                synced = false;
            }

            if (synced)
            {
                // Only the rows this sweep read: one enqueued for the same document AFTER the read is newer than
                // what was just indexed, and must survive to be synced again.
                await dbContext.SearchIndexOutbox
                    .Where(o => ids.Contains(o.Id))
                    .ExecuteDeleteAsync(cancellationToken);
                indexedCount++;
            }
            else
            {
                // Released rather than left to the lease, so a transient failure (OpenSearch briefly away) retries
                // at the next poll instead of five minutes later.
                await dbContext.SearchIndexOutbox
                    .Where(o => ids.Contains(o.Id))
                    .ExecuteUpdateAsync(set => set.SetProperty(o => o.ClaimedAt, (DateTimeOffset?)null), cancellationToken);
            }
        }

        if (indexedCount > 0)
        {
            _logger.LogInformation("Synced {Count} document(s) to the search index.", indexedCount);
        }

        return indexedCount > 0;
    }

    /// <summary>Puts claims nobody finished back in the queue, so a process killed mid-sync strands nothing.</summary>
    /// <remarks>
    /// The cutoff is applied in MEMORY, not in SQL: SQLite — the test provider — cannot compare a
    /// <c>DateTimeOffset</c> in SQL (the <c>EmailNotificationDispatcher</c> precedent). The claimed set is the rows
    /// being worked on right now, so reading it whole costs nothing. Two instances reclaiming the same row both set
    /// it back to null, which is the same outcome twice.
    /// </remarks>
    private async Task ReclaimAbandonedClaimsAsync(SimplArchiveDbContext dbContext, CancellationToken cancellationToken)
    {
        var claimed = await dbContext.SearchIndexOutbox.AsNoTracking()
            .Where(o => o.ClaimedAt != null)
            .Select(o => new { o.Id, o.ClaimedAt })
            .ToListAsync(cancellationToken);

        var cutoff = DateTimeOffset.UtcNow - ClaimLifetime;
        var stale = claimed.Where(o => o.ClaimedAt < cutoff).Select(o => o.Id).ToList();
        if (stale.Count == 0)
        {
            return;
        }

        var reclaimed = await dbContext.SearchIndexOutbox
            .Where(o => stale.Contains(o.Id) && o.ClaimedAt != null)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.ClaimedAt, (DateTimeOffset?)null), cancellationToken);

        _logger.LogWarning(
            "Reclaimed {Count} search-index claim(s) older than {Lifetime} — a previous sweep did not finish them. "
            + "The documents will be indexed again, which is harmless: indexing is idempotent.",
            reclaimed, ClaimLifetime);
    }
}
