using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Startup;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The startup well-known-mask backfill (#1340). It loops EVERY tenant on ONE DbContext, and our
// SaveChanges override runs ~10 validation/sync methods that each call ChangeTracker.Entries<T>() — which
// forces a full DetectChanges() over whatever is tracked. So an accumulating tracker makes tenant N's seed
// re-scan tenants 1…N−1: quadratic, and every sweep allocates.
//
// Measured before the fix: a host booting against 182 tenants / 4 378 masks pegged one core at ~98% for
// over an hour, in a continuous allocate→GC loop whose managed stack was exactly
// EnsureAssignabilityAsync → SaveChangesAsync → SyncBlockDocumentsAsync → ChangeTracker.Entries.
//
// The guard is the TRACKED-ENTITY COUNT, not a stopwatch: a timing assertion on a shared machine is a
// coin flip, while "the tracker does not grow with tenant count" is the actual invariant that keeps the
// work linear — and it fails loudly the moment someone removes the Clear().
public class TenantBackfillTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor());

    private static async Task<SqliteConnection> TenantsAsync(int count)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var setup = Ctx(connection);
        await setup.Database.EnsureCreatedAsync();
        for (var i = 0; i < count; i++)
        {
            setup.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {i}", CreatedAt = DateTimeOffset.UtcNow });
        }

        await setup.SaveChangesAsync();
        return connection;
    }

    [Fact]
    public async Task The_tracked_graph_stays_bounded_however_many_tenants_are_backfilled()
    {
        using var connection = await TenantsAsync(12);
        using var dbContext = Ctx(connection);
        var seeder = new WellKnownMaskSeeder(dbContext, NullLogger<WellKnownMaskSeeder>.Instance);

        await TenantBackfill.SeedWellKnownMasksAsync(dbContext, seeder);

        // One tenant's masks — not twelve tenants' worth. Before the fix this held everything every
        // previous tenant had seeded, which is precisely what made each SaveChanges more expensive than
        // the last. Zero is the honest expectation right after a Clear(); the headroom is for a future
        // backfill step that legitimately leaves its own last write tracked.
        var tracked = dbContext.ChangeTracker.Entries().Count();
        Assert.True(tracked < 50,
            $"the startup backfill left {tracked} entities tracked — it must clear per tenant (#1340), "
            + "or every SaveChanges re-scans every earlier tenant's graph.");
    }

    [Fact]
    public async Task Every_tenant_still_gets_its_well_known_masks()
    {
        // The clear must not cost correctness: the backfill's whole job is that no tenant is left behind.
        using var connection = await TenantsAsync(5);
        using var dbContext = Ctx(connection);
        var seeder = new WellKnownMaskSeeder(dbContext, NullLogger<WellKnownMaskSeeder>.Instance);

        await TenantBackfill.SeedWellKnownMasksAsync(dbContext, seeder);

        var tenantIds = await dbContext.Tenants.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        Assert.Equal(5, tenantIds.Count);
        foreach (var tenantId in tenantIds)
        {
            var masks = await dbContext.Masks.IgnoreQueryFilters().CountAsync(m => m.TenantId == tenantId);
            Assert.True(masks >= WellKnownMaskIds.FolderMasks.Count,
                $"tenant {tenantId} got {masks} well-known masks");
        }
    }
}
