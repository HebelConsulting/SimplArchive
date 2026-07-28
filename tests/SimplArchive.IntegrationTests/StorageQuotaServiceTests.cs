using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.IntegrationTests;

// Verifies the per-tenant storage-quota accounting service (ADR "Per-tenant storage quota"): CanStoreAsync honors
// the limit (unlimited when null), and AdjustUsageAsync increments/decrements the maintained counter atomically,
// clamping at zero. Soft-quota warnings (ADR "Storage soft-quota warnings") notify admins on crossing 80%/95%.
public class StorageQuotaServiceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenantAccessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenantAccessor);

    private static StorageQuotaService Svc(SimplArchiveDbContext ctx) =>
        new(ctx, NullLogger<StorageQuotaService>.Instance);

    private static async Task<(SqliteConnection Connection, Guid TenantId)> SeedAsync(long? quota, long used)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        using (var seed = CreateContext(connection, accessor))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, StorageQuotaBytes = quota, StorageUsedBytes = used });
            await seed.SaveChangesAsync();
        }

        return (connection, tenantId);
    }

    [Fact]
    public async Task CanStore_is_true_when_no_quota_is_set()
    {
        var (connection, tenantId) = await SeedAsync(quota: null, used: 5_000);
        using var _ = connection;
        using var ctx = CreateContext(connection, new CurrentTenantAccessor());

        Assert.True(await Svc(ctx).CanStoreAsync(tenantId, 1_000_000_000));
    }

    [Fact]
    public async Task CanStore_honors_the_limit_at_the_boundary()
    {
        var (connection, tenantId) = await SeedAsync(quota: 1_000, used: 900);
        using var _ = connection;
        using var ctx = CreateContext(connection, new CurrentTenantAccessor());
        var service = Svc(ctx);

        Assert.True(await service.CanStoreAsync(tenantId, 100));  // exactly fills → allowed
        Assert.False(await service.CanStoreAsync(tenantId, 101)); // one byte over → refused
    }

    [Fact]
    public async Task AdjustUsage_increments_decrements_and_clamps_at_zero()
    {
        var (connection, tenantId) = await SeedAsync(quota: 10_000, used: 0);
        using var _ = connection;
        var accessor = new CurrentTenantAccessor();

        using (var ctx = CreateContext(connection, accessor)) await Svc(ctx).AdjustUsageAsync(tenantId, 3_000);
        using (var ctx = CreateContext(connection, accessor)) await Svc(ctx).AdjustUsageAsync(tenantId, -1_000);
        using (var ctx = CreateContext(connection, accessor))
        {
            Assert.Equal(2_000, (await ctx.Tenants.SingleAsync(t => t.Id == tenantId)).StorageUsedBytes);
        }

        // A decrement larger than the counter clamps at zero (defensive against an un-counted blob being freed).
        using (var ctx = CreateContext(connection, accessor)) await Svc(ctx).AdjustUsageAsync(tenantId, -999_999);
        using (var ctx = CreateContext(connection, accessor))
        {
            Assert.Equal(0, (await ctx.Tenants.SingleAsync(t => t.Id == tenantId)).StorageUsedBytes);
        }
    }

    [Fact]
    public async Task Crossing_the_soft_quota_thresholds_warns_the_admin_and_re_arms_after_a_drop()
    {
        var (connection, tenantId) = await SeedAsync(quota: 1_000, used: 0);
        using var _ = connection;
        var accessor = new CurrentTenantAccessor();
        var adminId = Guid.NewGuid();
        using (var seed = CreateContext(connection, accessor))
        {
            seed.Users.Add(new User { Id = adminId, TenantId = tenantId, Email = "admin@acme.test", DisplayName = "Admin", IsTenantAdmin = true, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        async Task AdjustAsync(long delta) { using var ctx = CreateContext(connection, accessor); await Svc(ctx).AdjustUsageAsync(tenantId, delta); }
        async Task<int> WarningsAsync() { using var ctx = CreateContext(connection, accessor); return await ctx.Notifications.IgnoreQueryFilters().CountAsync(n => n.Type == NotificationType.StorageQuotaWarning && n.RecipientUserId == adminId); }
        async Task<int> LevelAsync() { using var ctx = CreateContext(connection, accessor); return (await ctx.Tenants.SingleAsync(t => t.Id == tenantId)).StorageWarningLevel; }

        // Below 80% → no warning.
        await AdjustAsync(700);
        Assert.Equal(0, await WarningsAsync());

        // Cross 80% → level 1 + one warning.
        await AdjustAsync(100); // 800 = 80%
        Assert.Equal(1, await LevelAsync());
        Assert.Equal(1, await WarningsAsync());

        // Cross 95% → level 2 + a second warning.
        await AdjustAsync(150); // 950 = 95%
        Assert.Equal(2, await LevelAsync());
        Assert.Equal(2, await WarningsAsync());

        // Staying above 95% (another add) doesn't re-warn.
        await AdjustAsync(20);
        Assert.Equal(2, await WarningsAsync());

        // Drop below 80% → level re-arms to 0, no new warning.
        await AdjustAsync(-600); // 370 = 37%
        Assert.Equal(0, await LevelAsync());
        Assert.Equal(2, await WarningsAsync());

        // Re-cross 80% → warns again (proves the re-arm).
        await AdjustAsync(450); // 820 = 82%
        Assert.Equal(1, await LevelAsync());
        Assert.Equal(3, await WarningsAsync());
    }
}
