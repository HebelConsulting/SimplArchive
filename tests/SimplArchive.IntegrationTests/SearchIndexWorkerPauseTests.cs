using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Search;

namespace SimplArchive.IntegrationTests;

// The #661 data-loss race, tested at its fix: the outbox worker writes THROUGH the alias, which during a
// rebuild points at the index the swap is about to delete — a row drained in that window succeeded, so it
// was removed, and then the swap took the document with the old index, leaving nothing anywhere that says
// so. The fix is a pause: while SearchReindexState.IsRunning, the worker holds every row, and they drain
// into the NEW index right after the swap.
//
// Tested HERE, not end-to-end, deliberately: the E2E fixture waits for the outbox to drain before handing
// the app to tests (#660), so no test can upload during a rebuild — the suite structurally cannot reproduce
// the race, which the issue records as the reason a regression would go unseen. The drain pass is public
// for exactly this reason.
public class SearchIndexWorkerPauseTests
{
    private sealed class RecordingIndexer : IDocumentIndexer
    {
        public readonly List<Guid> Synced = [];

        public Task<bool> SyncAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Synced.Add(documentId);
            return Task.FromResult(true);
        }

        public Task RemoveAsync(Guid documentId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (ServiceProvider Provider, RecordingIndexer Indexer, SearchReindexState State) Build(SqliteConnection connection)
    {
        var indexer = new RecordingIndexer();
        var state = new SearchReindexState();
        var services = new ServiceCollection();
        services.AddScoped(_ => new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor()));
        services.AddScoped<CurrentTenantAccessor>();
        services.AddScoped<IDocumentIndexer>(_ => indexer);
        return (services.BuildServiceProvider(), indexer, state);
    }

    [Fact]
    public async Task The_worker_holds_the_outbox_while_a_rebuild_runs_and_drains_after()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (provider, indexer, state) = Build(connection);
        using var _p = provider;

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            db.SearchIndexOutbox.Add(new SearchIndexOutbox
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DocumentId = documentId,
                EnqueuedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var worker = new SearchIndexWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), state, NullLogger<SearchIndexWorker>.Instance);

        // While the rebuild runs: nothing is synced, and — the half that loses data — the ROW SURVIVES. A
        // drain that synced into the doomed index would have deleted it, and the swap would then have taken
        // the document with nothing left anywhere to say so.
        state.IsRunning = true;
        Assert.False(await worker.DrainOnceAsync(CancellationToken.None));
        Assert.Empty(indexer.Synced);
        using (var scope = provider.CreateScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>()
                .SearchIndexOutbox.CountAsync());
        }

        // The swap done: the same row drains into the NEW index (the alias now points there), exactly once.
        state.IsRunning = false;
        Assert.True(await worker.DrainOnceAsync(CancellationToken.None));
        Assert.Equal([documentId], indexer.Synced);
        using (var scope = provider.CreateScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>()
                .SearchIndexOutbox.CountAsync());
        }
    }
}
