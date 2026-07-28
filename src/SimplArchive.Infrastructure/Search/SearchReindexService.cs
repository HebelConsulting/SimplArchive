using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimplArchive.Infrastructure.Search;

// Runs search-index rebuilds in the background (ADR 0139) — off the request path, one at a time. At startup
// it triggers an initial backfill if the alias doesn't exist yet (fresh deployment, or OpenSearch just
// enabled / its index lost); thereafter it processes admin-triggered requests from SearchReindexState.
// Registered only when OpenSearch is configured.
public sealed class SearchReindexService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchReindexState _state;
    private readonly ILogger<SearchReindexService> _logger;

    public SearchReindexService(IServiceScopeFactory scopeFactory, SearchReindexState state, ILogger<SearchReindexService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", nameof(SearchReindexService));
        await BackfillIfMissingAsync(stoppingToken);

        await foreach (var _ in _state.Requests.ReadAllAsync(stoppingToken))
        {
            try
            {
                _state.IsRunning = true;
                using var scope = _scopeFactory.CreateScope();
                var rebuilder = scope.ServiceProvider.GetRequiredService<OpenSearchIndexRebuilder>();
                _state.LastIndexedCount = await rebuilder.RebuildAsync(stoppingToken);
                _logger.LogInformation("Rebuilt the search index with {Count} document(s).", _state.LastIndexedCount);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Search index rebuild failed.");
            }
            finally
            {
                _state.IsRunning = false;
            }
        }
    }

    // Waits for OpenSearch to become reachable (it may still be starting), then triggers a backfill if the
    // alias is absent. A connection failure is a retry; a reachable "alias missing" enqueues the rebuild.
    private async Task BackfillIfMissingAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 20 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var rebuilder = scope.ServiceProvider.GetRequiredService<OpenSearchIndexRebuilder>();
                if (!await rebuilder.AliasExistsAsync(stoppingToken))
                {
                    _logger.LogInformation("Search alias missing at startup — running an initial backfill.");
                    _state.Request();
                }

                return; // OpenSearch answered — decision made
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }

        _logger.LogWarning("OpenSearch was not reachable at startup; skipping the initial backfill check.");
    }
}
