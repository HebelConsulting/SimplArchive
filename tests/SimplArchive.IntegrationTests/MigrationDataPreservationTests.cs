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
//
// Raw SQL counts too. A DropColumn/DropTable is what EF models as an operation, but `migrationBuilder.Sql` can
// delete just as much data while presenting as an opaque SqlOperation — which is how a full "DELETE FROM
// AuditEvents" passed this guard unremarked. Destructive verbs in raw SQL are therefore matched textually and
// need the same allowlist entry, so the gate can't be walked around by writing the destruction out by hand.
public class MigrationDataPreservationTests
{
    // Matched against the SQL of any SqlOperation in an Up(). Word-bounded so an INSERT of a row whose text
    // happens to contain "delete" doesn't trip it.
    private static readonly string[] DestructiveSqlVerbs = ["DELETE FROM", "TRUNCATE", "DROP TABLE", "DROP COLUMN"];

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
        // Hash-chained audit log (ADR 0318): wipes AuditEvents, because pre-chain rows have no hash and could
        // never be verified — leaving them would make the chain unverifiable from its first link. Predates the
        // raw-SQL half of this guard and is recorded here now that the guard can see it.
        "20260717182120_AddAuditChain",
        // Renumbered chat kinds (ADR 0545): deletes the retired DocumentFiled rows, each of which duplicated the
        // VersionFiled row beside it — same document, same author, same moment. No thread loses information; it
        // stops each filing being announced twice.
        "20260807155503_RenumberChatMessageKinds",
        // External links are person-only (ADR 0546): drops ServiceAccount.CanCreateExternalLink and the link's
        // service-account creator column, and deletes any link a service account created — such a link cannot be
        // re-attributed to a person, and a live share whose creator cannot be named is the thing being prevented.
        "20260807164245_ExternalLinksArePersonOnly",
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
                .Where(IsDestructive)
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

    private static bool IsDestructive(MigrationOperation op) => op switch
    {
        DropColumnOperation or DropTableOperation => true,
        SqlOperation sql => DestructiveSqlVerbs.Any(
            verb => sql.Sql.Contains(verb, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static string Describe(MigrationOperation op) => op switch
    {
        DropColumnOperation c => $"DropColumn {c.Table}.{c.Name}",
        DropTableOperation t => $"DropTable {t.Name}",
        SqlOperation sql => $"Sql {Condense(sql.Sql)}",
        _ => op.GetType().Name,
    };

    // Migration SQL is written as a formatted block; a violation message is more useful on one line.
    private static string Condense(string sql)
    {
        var oneLine = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 120 ? oneLine : $"{oneLine[..120]}…";
    }
}
