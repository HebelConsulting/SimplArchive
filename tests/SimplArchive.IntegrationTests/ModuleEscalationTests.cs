using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// ADR 0740's escalate → grace → self-deactivate ladder against the real DbContext: each upward
// level-cross is announced to every active tenant admin exactly once (the storage-warning shape), a
// repeat sweep is silent, and a renewal re-arms the ladder without an announcement. Deterministic — the
// sweep takes its clock as a parameter.
public class ModuleEscalationTests
{
    private static readonly DateTimeOffset ContractEnd = new(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;
        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private sealed record Rig(SimplArchiveDbContext Context, ModuleEscalationService Service, Guid TenantId, Guid AdminId, Guid SecondAdminId, ModuleActivation Activation);

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var secondAdminId = Guid.NewGuid();
        var context = CreateContext(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = adminId, TenantId = tenantId, Email = "admin@example.com", DisplayName = "Admin", IsTenantAdmin = true, CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = secondAdminId, TenantId = tenantId, Email = "admin2@example.com", DisplayName = "Admin 2", IsTenantAdmin = true, CreatedAt = DateTimeOffset.UtcNow });
        // Neither of these two may ever be notified: one is no admin, one is deactivated.
        context.Users.Add(new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = "user@example.com", DisplayName = "User", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = "gone@example.com", DisplayName = "Gone", IsTenantAdmin = true, IsActive = false, CreatedAt = DateTimeOffset.UtcNow });
        var activation = new ModuleActivation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ModuleId = "test-module",
            SupportContractEndDate = ContractEnd,
            LicenseDocumentId = Guid.NewGuid(),
            ActivatedAt = DateTimeOffset.UtcNow,
        };
        context.ModuleActivations.Add(activation);
        await context.SaveChangesAsync();
        return new Rig(context, new ModuleEscalationService(context, NullLogger<ModuleEscalationService>.Instance), tenantId, adminId, secondAdminId, activation);
    }

    private static Task<List<Notification>> EscalationsAsync(SimplArchiveDbContext context) =>
        context.Notifications.Where(n => n.Type == NotificationType.ModuleLicenseEscalation).ToListAsync();

    [Fact]
    public async Task An_upward_cross_is_announced_to_every_active_admin_exactly_once()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        // Inside the 30-day warn window: level 0 → 1.
        var inWarnWindow = ContractEnd.AddDays(-10);
        Assert.Equal(2, await rig.Service.SweepAsync(inWarnWindow));

        var notifications = await EscalationsAsync(rig.Context);
        Assert.Equal(2, notifications.Count); // both active admins — never the plain user, never the deactivated admin
        Assert.Equal(new[] { rig.AdminId, rig.SecondAdminId }.Order(), notifications.Select(n => n.RecipientUserId).Order());
        Assert.All(notifications, n => Assert.Contains("2026-12-31", n.Body));

        // The same instant again: the level is remembered, the sweep is silent.
        Assert.Equal(0, await rig.Service.SweepAsync(inWarnWindow));
        Assert.Equal(2, (await EscalationsAsync(rig.Context)).Count);
    }

    [Fact]
    public async Task Each_rung_announces_with_its_own_words()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        await rig.Service.SweepAsync(ContractEnd.AddDays(-10));          // → 1: ends soon
        await rig.Service.SweepAsync(ContractEnd.AddDays(2));            // → 2: grace, naming the deactivation date
        await rig.Service.SweepAsync(ContractEnd.AddDays(40));           // → 3: deactivated

        var bodies = (await EscalationsAsync(rig.Context)).OrderBy(n => n.CreatedAt).Select(n => n.Title).Distinct().ToList();
        Assert.Equal(3, bodies.Count);
        Assert.Contains(bodies, t => t.Contains("ends soon"));
        Assert.Contains(bodies, t => t.Contains("has ended"));
        Assert.Contains(bodies, t => t.Contains("deactivated"));
    }

    [Fact]
    public async Task A_renewal_rearms_the_ladder_silently()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        await rig.Service.SweepAsync(ContractEnd.AddDays(40)); // straight to 3 — one announcement per admin
        Assert.Equal(2, (await EscalationsAsync(rig.Context)).Count);

        // The renewal: a new contract end a year out (what ActivateAsync writes, minus the license plumbing).
        rig.Activation.SupportContractEndDate = ContractEnd.AddYears(1);
        await rig.Context.SaveChangesAsync();

        // The next sweep re-arms to 0 and announces NOTHING — silence is the renewal's answer.
        Assert.Equal(0, await rig.Service.SweepAsync(ContractEnd.AddDays(41)));
        Assert.Equal(0, (await rig.Context.ModuleActivations.SingleAsync()).EscalationLevel);
        Assert.Equal(2, (await EscalationsAsync(rig.Context)).Count);
    }
}
