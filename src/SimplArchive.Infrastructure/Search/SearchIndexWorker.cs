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
    private readonly ILogger<SearchIndexWorker> _logger;

    public SearchIndexWorker(IServiceScopeFactory scopeFactory, ILogger<SearchIndexWorker> logger)
    {
        _scopeFactory = scopeFactory;
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

    private async Task<bool> DrainOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>();
        var indexer = scope.ServiceProvider.GetRequiredService<IDocumentIndexer>();

        var batch = await dbContext.SearchIndexOutbox
            .OrderBy(o => o.EnqueuedAt)
            .ThenBy(o => o.Id)
            .Take(BatchSize)
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
