using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Search;

namespace SimplArchive.IntegrationTests;

// #1587, ADR 0836: the outbox worker runs in BOTH app instances (ADR 0808), so it claims each document's rows
// before syncing. Measured on the kiosk before this: one row the other instance had already deleted threw from
// a SaveChanges covering the whole batch, discarding the deletes for every other document in it — across
// tenants — behind a warning naming neither. Each case below pins one property of the per-document claim.
public class SearchIndexWorkerClaimTests
{
    private sealed class ScriptedIndexer : IDocumentIndexer
    {
        public readonly List<Guid> Synced = [];
        public Guid? Throws { get; init; }
        public Func<Guid, Task>? DuringSync { get; set; }

        public async Task<bool> SyncAsync(Guid documentId, CancellationToken cancellationToken)
        {
            if (documentId == Throws)
            {
                throw new HttpRequestException("search backend unreachable");
            }

            if (DuringSync is { } hook)
            {
                await hook(documentId);
            }

            Synced.Add(documentId);
            return true;
        }

        public Task RemoveAsync(Guid documentId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record Rig(ServiceProvider Provider, SearchIndexWorker Worker, Guid TenantId) : IDisposable
    {
        public void Dispose() => Provider.Dispose();

        public async Task<Guid> EnqueueAsync(Guid documentId, DateTimeOffset? claimedAt = null)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var id = Guid.NewGuid();
            db.SearchIndexOutbox.Add(new SearchIndexOutbox
            {
                Id = id,
                TenantId = TenantId,
                DocumentId = documentId,
                EnqueuedAt = DateTimeOffset.UtcNow,
                ClaimedAt = claimedAt,
            });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<List<SearchIndexOutbox>> RowsAsync()
        {
            using var scope = Provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>().SearchIndexOutbox.AsNoTracking().ToListAsync();
        }
    }

    private static async Task<Rig> BuildAsync(SqliteConnection connection, ScriptedIndexer indexer)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor()));
        services.AddScoped<CurrentTenantAccessor>();
        services.AddScoped<IDocumentIndexer>(_ => indexer);
        var provider = services.BuildServiceProvider();

        var tenantId = Guid.NewGuid();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var worker = new SearchIndexWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), new SearchReindexState(), NullLogger<SearchIndexWorker>.Instance);
        return new Rig(provider, worker, tenantId);
    }

    [Fact]
    public async Task A_document_claimed_by_the_other_instance_is_skipped_and_the_rest_drain()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var indexer = new ScriptedIndexer();
        using var rig = await BuildAsync(connection, indexer);

        var claimedElsewhere = Guid.NewGuid();
        var free = Guid.NewGuid();
        var heldRow = await rig.EnqueueAsync(claimedElsewhere, claimedAt: DateTimeOffset.UtcNow);
        await rig.EnqueueAsync(free);

        Assert.True(await rig.Worker.DrainOnceAsync(CancellationToken.None));

        Assert.Equal([free], indexer.Synced);
        var left = Assert.Single(await rig.RowsAsync());
        Assert.Equal(heldRow, left.Id);
        Assert.NotNull(left.ClaimedAt); // still the other instance's, untouched
    }

    // THE MEASURED BUG: one document's failure used to cost every other document in the batch its delete.
    [Fact]
    public async Task One_failing_document_does_not_discard_the_others_and_is_released_for_retry()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var failing = Guid.NewGuid();
        var indexer = new ScriptedIndexer { Throws = failing };
        using var rig = await BuildAsync(connection, indexer);

        var failingRow = await rig.EnqueueAsync(failing);
        var others = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var doc in others)
        {
            await rig.EnqueueAsync(doc);
        }

        Assert.True(await rig.Worker.DrainOnceAsync(CancellationToken.None));

        Assert.Equal(others.Order(), indexer.Synced.Order());
        var left = Assert.Single(await rig.RowsAsync());
        Assert.Equal(failingRow, left.Id);
        Assert.Null(left.ClaimedAt); // released, so the next poll retries it rather than waiting out the lease
    }

    [Fact]
    public async Task A_claim_older_than_the_lease_is_reclaimed_and_drained()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var indexer = new ScriptedIndexer();
        using var rig = await BuildAsync(connection, indexer);

        var orphan = Guid.NewGuid();
        await rig.EnqueueAsync(orphan, claimedAt: DateTimeOffset.UtcNow - SearchIndexWorker.ClaimLifetime - TimeSpan.FromMinutes(1));

        Assert.True(await rig.Worker.DrainOnceAsync(CancellationToken.None));

        Assert.Equal([orphan], indexer.Synced);
        Assert.Empty(await rig.RowsAsync());
    }

    // The delete names the rows this sweep READ, never "every row for the document": one enqueued while the sync
    // ran describes a newer state than the one just indexed, and must survive to be synced again.
    [Fact]
    public async Task A_row_enqueued_for_the_same_document_during_its_sync_survives()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var indexer = new ScriptedIndexer();
        using var rig = await BuildAsync(connection, indexer);

        var document = Guid.NewGuid();
        await rig.EnqueueAsync(document);
        Guid? newer = null;
        indexer.DuringSync = async doc => newer ??= await rig.EnqueueAsync(doc);

        Assert.True(await rig.Worker.DrainOnceAsync(CancellationToken.None));

        var left = Assert.Single(await rig.RowsAsync());
        Assert.Equal(newer, left.Id);
        Assert.Null(left.ClaimedAt);
    }
}
