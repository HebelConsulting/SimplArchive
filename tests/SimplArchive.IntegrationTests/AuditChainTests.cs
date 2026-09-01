using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Audit;
using SimplArchive.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimplArchive.IntegrationTests;

// Verifies the per-tenant audit hash chain (ADR "Audit trail hash chain"): recorded events form a chain the
// verifier accepts; editing a stored field or deleting a row is detected, with the first break reported; the
// chain is per-tenant (each tenant starts at Sequence 0). The DateTimeOffset round-trip through the store is
// covered implicitly — a timestamp mismatch would fail the recompute.
public class AuditChainTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenantAccessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenantAccessor);

    private static AuditRecorder CreateRecorder(SimplArchiveDbContext db, CurrentTenantAccessor tenant, CurrentUserAccessor user) =>
        new(db, user, new CurrentServiceAccountAccessor(), new CurrentPlatformAdministratorAccessor(), tenant, new CurrentImpersonationAccessor(), TimeProvider.System, NullLogger<AuditRecorder>.Instance);

    [Fact]
    public async Task Recorded_chain_verifies_and_tampering_is_detected()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();

        using (var setup = CreateContext(connection, tenantAccessor))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "a@acme.test", DisplayName = "Alice", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(actor);
            await seed.SaveChangesAsync();
        }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        // Record a handful of events → a chain of Sequence 0..4.
        using (var record = CreateContext(connection, tenantAccessor))
        {
            var recorder = CreateRecorder(record, tenantAccessor, userAccessor);
            for (var i = 0; i < 5; i++)
            {
                await recorder.RecordAsync($"Test.Action{i}", "Document", Guid.NewGuid(), $"doc {i}", $"detail {i}");
            }
        }

        // The chain is contiguous and verifies clean.
        using (var check = CreateContext(connection, tenantAccessor))
        {
            var sequences = await check.AuditEvents.OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync();
            Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, sequences);

            var result = await new AuditChainVerifier(check, tenantAccessor).VerifyAsync();
            Assert.True(result.Valid);
            Assert.Equal(5, result.CheckedCount);
            Assert.Null(result.BrokenAtSequence);
        }

        // Tamper: edit a stored event's Details directly (bypassing the recorder) → the recompute mismatches.
        using (var tamper = CreateContext(connection, tenantAccessor))
        {
            var target = await tamper.AuditEvents.OrderBy(e => e.Sequence).Skip(2).FirstAsync();
            target.Details = "tampered";
            await tamper.SaveChangesAsync();
        }

        using (var check = CreateContext(connection, tenantAccessor))
        {
            var result = await new AuditChainVerifier(check, tenantAccessor).VerifyAsync();
            Assert.False(result.Valid);
            Assert.Equal(2, result.BrokenAtSequence);
        }
    }

    [Fact]
    public async Task Deleting_a_row_is_detected_as_a_gap()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "a@acme.test", DisplayName = "Alice", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.Add(tenant); seed.Users.Add(actor); await seed.SaveChangesAsync(); }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;
        using (var record = CreateContext(connection, tenantAccessor))
        {
            var recorder = CreateRecorder(record, tenantAccessor, userAccessor);
            for (var i = 0; i < 4; i++) await recorder.RecordAsync($"Test.Action{i}");
        }

        // Delete the Sequence-1 event → a gap; verification breaks at the next surviving row.
        using (var tamper = CreateContext(connection, tenantAccessor))
        {
            var target = await tamper.AuditEvents.SingleAsync(e => e.Sequence == 1);
            tamper.AuditEvents.Remove(target);
            await tamper.SaveChangesAsync();
        }

        using (var check = CreateContext(connection, tenantAccessor))
        {
            var result = await new AuditChainVerifier(check, tenantAccessor).VerifyAsync();
            Assert.False(result.Valid);
            Assert.Equal(2, result.BrokenAtSequence); // first surviving row whose Sequence != expected
        }
    }

    [Fact]
    public async Task Chain_is_per_tenant_each_starting_at_zero()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var t1 = new Tenant { Id = Guid.NewGuid(), Name = "T1", CreatedAt = DateTimeOffset.UtcNow };
        var t2 = new Tenant { Id = Guid.NewGuid(), Name = "T2", CreatedAt = DateTimeOffset.UtcNow };
        var u1 = new User { Id = Guid.NewGuid(), TenantId = t1.Id, Email = "u1@t.test", DisplayName = "U1", CreatedAt = DateTimeOffset.UtcNow };
        var u2 = new User { Id = Guid.NewGuid(), TenantId = t2.Id, Email = "u2@t.test", DisplayName = "U2", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.AddRange(t1, t2); seed.Users.AddRange(u1, u2); await seed.SaveChangesAsync(); }

        // Two events in t1, one in t2.
        tenantAccessor.TenantId = t1.Id; userAccessor.UserId = u1.Id;
        using (var r = CreateContext(connection, tenantAccessor)) { var rec = CreateRecorder(r, tenantAccessor, userAccessor); await rec.RecordAsync("A"); await rec.RecordAsync("B"); }
        tenantAccessor.TenantId = t2.Id; userAccessor.UserId = u2.Id;
        using (var r = CreateContext(connection, tenantAccessor)) { var rec = CreateRecorder(r, tenantAccessor, userAccessor); await rec.RecordAsync("C"); }

        // Each tenant's chain is independent and starts at 0; both verify clean.
        tenantAccessor.TenantId = t1.Id;
        using (var check = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(new long[] { 0, 1 }, await check.AuditEvents.OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync());
            Assert.True((await new AuditChainVerifier(check, tenantAccessor).VerifyAsync()).Valid);
        }
        tenantAccessor.TenantId = t2.Id;
        using (var check = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(new long[] { 0 }, await check.AuditEvents.OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync());
            Assert.True((await new AuditChainVerifier(check, tenantAccessor).VerifyAsync()).Valid);
        }
    }
}
