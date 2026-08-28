using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SimplArchive.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SimplArchive.EndToEndTests;

// End-to-end on a real Postgres, proving the migration chain is data-preserving (ADR "Data-preserving
// migrations"): migrate a fresh database to an EARLY migration, insert a row, migrate the rest of the way to
// head, and confirm the row survived with the later-added columns backfilled to their defaults. Self-contained
// (its own throwaway Postgres container). Needs Docker.
[Trait("Area", "e2e-1")]
public class MigrationRoundTripTests : IAsyncLifetime
{
    // The first migration that creates the Tenants table (Id/Name/Status/CreatedAt/DeactivatedAt).
    private const string EarlyMigration = "20260712091350_AddCoreDomainEntities";

    private PostgreSqlContainer _postgres = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("simplarchive")
            .Build();
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private SimplArchiveDbContext Context() =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options,
            new CurrentTenantAccessor());

    [Fact]
    public async Task A_row_seeded_at_an_early_migration_survives_migrating_to_head()
    {
        var tenantId = Guid.NewGuid();

        // Migrate only up to the early migration, then insert a Tenant with that version's columns (raw SQL, as
        // the entity model is at head).
        await using (var context = Context())
        {
            await context.GetService<IMigrator>().MigrateAsync(EarlyMigration);
            await context.Database.ExecuteSqlRawAsync(
                """INSERT INTO "Tenants" ("Id", "Name", "Status", "CreatedAt", "DeactivatedAt") VALUES ({0}, 'Legacy Tenant', 0, now(), NULL)""",
                tenantId);
        }

        // Migrate the rest of the way to head.
        await using (var context = Context())
        {
            await context.Database.MigrateAsync();
        }

        // The row survived, and columns added by later migrations were backfilled to their store defaults.
        await using (var context = Context())
        {
            var tenant = await context.Tenants.SingleAsync(t => t.Id == tenantId);
            Assert.Equal("Legacy Tenant", tenant.Name);
            Assert.Equal(365, tenant.AuditRetentionDays);          // AddAuditRetention default
            Assert.Equal(0, tenant.CheckoutTtlDays);               // AddCheckoutTtl default
            Assert.Equal(-1, tenant.AuditWormArchivedThrough);     // AddAuditWormCheckpoint default
            Assert.False(tenant.RequireMfa);                       // AddTenantRequireMfa default
            Assert.False(string.IsNullOrEmpty(tenant.DefaultOcrLanguages)); // AddOcrLanguageSettings default
        }
    }
}
