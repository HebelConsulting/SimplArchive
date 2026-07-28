using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Verifies audit retention & purge (ADR "Audit trail retention and purge"): purge deletes the oldest
// contiguous prefix past the window, never the chain tip, advances the retained-window checkpoint, and the
// hash chain still verifies afterwards (a purge is not tampering). Retention 0 keeps everything. The chain is
// built manually with controlled timestamps (the recorder always stamps "now"), using the real hasher so the
// links are genuine.
public class AuditRetentionTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenantAccessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenantAccessor);

    [Fact]
    public async Task Purge_removes_aged_prefix_keeps_tip_and_chain_still_verifies()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, AuditRetentionDays = 30 };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.Add(tenant); await seed.SaveChangesAsync(); }

        // Six events: 0..2 are ~100 days old, 3..5 are recent.
        var now = DateTimeOffset.UtcNow;
        using (var build = CreateContext(connection, tenantAccessor))
        {
            var prev = AuditEventHasher.Genesis;
            for (var i = 0; i < 6; i++)
            {
                var ts = i < 3 ? now.AddDays(-100).AddMinutes(i) : now.AddMinutes(i);
                prev = AppendManual(build, tenant.Id, i, ts, $"Test.Action{i}", prev);
            }
            await build.SaveChangesAsync();
        }

        // Purge with a 30-day window → the three old events (Sequence 0..2) go; 3..5 stay.
        using (var purgeContext = CreateContext(connection, tenantAccessor))
        {
            var purged = await new AuditRetentionService(purgeContext).PurgeAsync(tenant.Id);
            Assert.Equal(3, purged);
        }

        tenantAccessor.TenantId = tenant.Id;
        using (var check = CreateContext(connection, tenantAccessor))
        {
            var remaining = await check.AuditEvents.OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync();
            Assert.Equal(new long[] { 3, 4, 5 }, remaining);

            var reloaded = await check.Tenants.SingleAsync(t => t.Id == tenant.Id);
            Assert.Equal(3, reloaded.AuditChainStartSequence);
            Assert.NotNull(reloaded.AuditLastPurgedAt);

            // The chain still verifies from the advanced checkpoint — a purge is not tampering.
            var result = await new AuditChainVerifier(check, tenantAccessor).VerifyAsync();
            Assert.True(result.Valid);
            Assert.Equal(3, result.CheckedCount);
        }
    }

    [Fact]
    public async Task Purge_never_deletes_the_chain_tip_even_when_all_events_are_old()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, AuditRetentionDays = 30 };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.Add(tenant); await seed.SaveChangesAsync(); }

        var old = DateTimeOffset.UtcNow.AddDays(-200);
        using (var build = CreateContext(connection, tenantAccessor))
        {
            var prev = AuditEventHasher.Genesis;
            for (var i = 0; i < 4; i++) prev = AppendManual(build, tenant.Id, i, old.AddMinutes(i), $"A{i}", prev);
            await build.SaveChangesAsync();
        }

        using (var purgeContext = CreateContext(connection, tenantAccessor))
        {
            var purged = await new AuditRetentionService(purgeContext).PurgeAsync(tenant.Id);
            Assert.Equal(3, purged); // 0..2 purged; the tip (3) is always kept
        }

        tenantAccessor.TenantId = tenant.Id;
        using (var check = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(new long[] { 3 }, await check.AuditEvents.Select(e => e.Sequence).ToListAsync());
            Assert.True((await new AuditChainVerifier(check, tenantAccessor).VerifyAsync()).Valid);
        }
    }

    [Fact]
    public async Task Retention_zero_keeps_everything()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        // Create with the default then set 0 via an update — mirrors the PUT /retention path, and avoids the
        // HasDefaultValue "CLR-default on insert uses the store default" gotcha (a fresh insert of 0 → stored 365).
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.Add(tenant); await seed.SaveChangesAsync(); }
        using (var upd = CreateContext(connection, tenantAccessor)) { var t = await upd.Tenants.SingleAsync(x => x.Id == tenant.Id); t.AuditRetentionDays = 0; await upd.SaveChangesAsync(); }

        var old = DateTimeOffset.UtcNow.AddDays(-500);
        using (var build = CreateContext(connection, tenantAccessor))
        {
            var prev = AuditEventHasher.Genesis;
            for (var i = 0; i < 3; i++) prev = AppendManual(build, tenant.Id, i, old.AddMinutes(i), $"A{i}", prev);
            await build.SaveChangesAsync();
        }

        using (var purgeContext = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(0, await new AuditRetentionService(purgeContext).PurgeAsync(tenant.Id));
        }

        tenantAccessor.TenantId = tenant.Id;
        using var readContext = CreateContext(connection, tenantAccessor);
        Assert.Equal(3, await readContext.AuditEvents.CountAsync());
    }

    private static string AppendManual(SimplArchiveDbContext db, Guid tenantId, long sequence, DateTimeOffset timestamp, string action, string previousHash)
    {
        var e = new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Sequence = sequence,
            Timestamp = AuditEventHasher.TruncateToMicroseconds(timestamp),
            ActorType = AuditActorType.User,
            ActorId = Guid.NewGuid(),
            ActorName = "Alice",
            Action = action,
            Hash = "",
        };
        e.Hash = AuditEventHasher.ComputeHash(previousHash, e);
        db.AuditEvents.Add(e);
        return e.Hash;
    }
}
