using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The data-preserving-migration guardrail (ADR "Data-preserving migrations"): a migration must not drop a table
// or column in its Up() — that loses data. Inspects each migration's actual EF UpOperations (robust, not a text
// scan). Two migrations predate the policy (pre-real-data, ADR 0200 era) and are grandfathered; any NEW
// destructive migration must be added here deliberately, with a documented reason — which is the reviewed act
// this test forces.
public class MigrationDataPreservationTests
{
    private static readonly HashSet<string> DestructiveAllowlist = new()
    {
        // Reshaped the mask model before any real data existed.
        "20260712105112_AddMaskVersioning",
        // Repository/Document unification — removed the Repository entity (ADR 0200), no real deployment yet.
        "20260712201321_RepositoryDocumentUnification",
        // Scoped saved-search sharing (ADR 0425): folds the all-tenant SavedSearch.IsShared bool into the new
        // ShareScope enum (true → Everyone) and drops IsShared — data-preserving via a backfill before the drop.
        "20260723162456_AddSavedSearchScopedSharing",
        // Configurable sensitivity labels (ADR 0426→0427-era): replaces the Document.SensitivityLabel int enum
        // with a nullable FK to the new per-tenant SensitivityLabelDefinition — data-preserving (seeds the four
        // defaults + backfills by rank before the drop).
        "20260723202530_AddConfigurableSensitivityLabels",
    };

    private static SimplArchiveDbContext MetadataContext() =>
        // A Npgsql context purely for migrations metadata — no connection is opened to read UpOperations.
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseNpgsql("Host=localhost;Database=metadata-only").Options,
            new CurrentTenantAccessor());

    [Fact]
    public void No_migration_drops_a_table_or_column_in_up_unless_allowlisted()
    {
        using var context = MetadataContext();
        var assembly = context.GetService<IMigrationsAssembly>();
        var provider = context.Database.ProviderName!;

        var violations = new List<string>();
        foreach (var (id, typeInfo) in assembly.Migrations)
        {
            if (DestructiveAllowlist.Contains(id))
            {
                continue;
            }

            var migration = assembly.CreateMigration(typeInfo, provider);
            var drops = migration.UpOperations
                .Where(op => op is DropColumnOperation or DropTableOperation)
                .Select(Describe)
                .ToList();
            if (drops.Count > 0)
            {
                violations.Add($"{id}: {string.Join(", ", drops)}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Migrations must be data-preserving (ADR \"Data-preserving migrations\"). These drop a table/column in "
            + "Up() and are not allowlisted — reshape without data loss, or add to the allowlist with a reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void The_allowlist_only_names_real_migrations()
    {
        using var context = MetadataContext();
        var ids = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToHashSet();

        // A stale allowlist entry (typo / renamed migration) would silently weaken the guardrail.
        Assert.All(DestructiveAllowlist, id => Assert.Contains(id, ids));
    }

    private static string Describe(MigrationOperation op) => op switch
    {
        DropColumnOperation c => $"DropColumn {c.Table}.{c.Name}",
        DropTableOperation t => $"DropTable {t.Name}",
        _ => op.GetType().Name,
    };
}
