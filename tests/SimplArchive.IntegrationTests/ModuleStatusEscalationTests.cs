using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The FIRING half of the escalation ladder (ADR 0753/#5): the status-sweep finds a subject sitting in an
// escalating status, hands it to the module's handler, and files an in-app notification for the recipient the
// module named — once. The idempotency is the module's marker (ABI 0.5), so a second sweep sends nothing, and
// a recipient with no account here is skipped rather than crashing the sweep.
public class ModuleStatusEscalationTests
{
    private const string RecipientEmail = "watcher@example.com";

    private sealed class UserAccessor : ICurrentUserAccessor { public Guid? UserId { get; set; } }
    private sealed class ServiceAccountAccessor : ICurrentServiceAccountAccessor { public Guid? ServiceAccountId { get; set; } }

    private static SimplArchiveDbContext Context(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Rig(
        SimplArchiveDbContext Db, CurrentTenantAccessor Tenant, ModuleStatusEscalationService Sweep, Guid RecipientId);

    private static async Task<Rig> RigAsync(SqliteConnection connection, string certValidTo)
    {
        using (var setup = Context(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var actingUserId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var rootId = Guid.NewGuid();

        using (var seed = Context(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = actingUserId, TenantId = tenantId, Email = "acting@example.com", DisplayName = "Acting", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = recipientId, TenantId = tenantId, Email = RecipientEmail, DisplayName = "Watcher", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Root", CreatedByUserId = actingUserId, CreatedAt = DateTimeOffset.UtcNow });
            // An ACTIVE licence: contract ends in the future, so ModuleActivationPolicy.IsActive holds.
            seed.ModuleActivations.Add(new ModuleActivation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ModuleId = "test-module",
                SupportContractEndDate = DateTimeOffset.UtcNow.AddDays(365),
                LicenseDocumentId = Guid.NewGuid(),
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var module = new TestModule.TestModule();
        using (var seedContext = Context(connection, tenantId))
        {
            await new ModuleMaskSeeder(seedContext, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
        }

        // The machines carry the module id (the sweep filters on active module) — declared through ForModule.
        var catalog = new StateMachineCatalog();
        module.DefineStateMachines(catalog.ForModule(module.ModuleId));

        var tenantAccessor = new CurrentTenantAccessor { TenantId = tenantId };
        var db = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenantAccessor);

        var facade = new ModuleArchiveFacade(db, new UserAccessor { UserId = actingUserId }, new ServiceAccountAccessor());
        var engine = new StateMachineEngine(db, catalog, facade, new ServiceCollection().BuildServiceProvider());
        var notifications = new NotificationService(db, tenantAccessor, new UserAccessor { UserId = actingUserId });
        var sweep = new ModuleStatusEscalationService(db, tenantAccessor, engine, catalog, notifications,
            NullLogger<ModuleStatusEscalationService>.Instance);

        // A dossier with a certificate whose expiry the test controls (Expiring = within 30 days).
        var dossierId = await facade.CreateDocumentAsync(rootId, TestModule.TestModule.DossierMaskId, "Dossier");
        await facade.CreateDocumentAsync(dossierId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = certValidTo });

        TestModule.TestModule.EscalationRecipientEmail = RecipientEmail;
        return new Rig(db, tenantAccessor, sweep, recipientId);
    }

    [Fact]
    public async Task An_expiring_subject_notifies_the_named_recipient_once()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var now = DateTimeOffset.UtcNow;
        var rig = await RigAsync(connection, certValidTo: now.AddDays(15).ToString("yyyy-MM-dd")); // inside the 30-day window

        var firstSent = await rig.Sweep.SweepAsync(now);

        Assert.Equal(1, firstSent);
        var notification = Assert.Single(await rig.Db.Notifications.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(rig.RecipientId, notification.RecipientUserId);
        Assert.Equal(NotificationType.ModuleStatusEscalation, notification.Type);
        Assert.Contains("expires", notification.Body);

        // Idempotent: the module's marker records the deadline warned about, so a second sweep sends nothing.
        var secondSent = await rig.Sweep.SweepAsync(now);
        Assert.Equal(0, secondSent);
        Assert.Single(await rig.Db.Notifications.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_subject_that_is_not_expiring_notifies_nobody()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var now = DateTimeOffset.UtcNow;
        var rig = await RigAsync(connection, certValidTo: now.AddDays(200).ToString("yyyy-MM-dd")); // far future — not Expiring

        var sent = await rig.Sweep.SweepAsync(now);

        Assert.Equal(0, sent);
        Assert.Empty(await rig.Db.Notifications.IgnoreQueryFilters().ToListAsync());
    }
}
