using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The proof that moving containment enforcement onto the model (#673, ADR 0655) changed no verdict.
//
// "The existing tests still pass" is a weaker claim than it sounds — those cover the cases somebody thought to
// write. This covers the whole space: every (parent mask, child mask) pair over every well-known mask, plus the
// no-parent case, comparing the STATIC reading the invariant used to perform against the REAL
// MaskContainmentRules.Verify, loaded from a really-seeded tenant.
//
// Driving the real Verify — rather than a second implementation written here to agree with it — is what makes
// this worth having: it exercises the seed, the load and the decision together, so a rule that never reached
// the database fails here rather than silently reading as "unrestricted".
//
// STANDING, not temporary. This was written as a sequencing test — the thing you keep until the static tables
// go — but they are not going: they remain the SEED for the well-known masks (owner-decided 2026-08-20), so
// the two representations of one rule coexist permanently. That is exactly the arrangement that drifts, and a
// drift here is invisible: the seed would go on writing rows nobody notices are wrong, and the invariant would
// enforce them. So this is the standing check that the seed still reproduces the rules it was derived from,
// and it earns its place for as long as both exist rather than only until one of them stops.
public class MaskContainmentEquivalenceTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<MaskContainmentRules> SeededRulesAsync(SqliteConnection connection)
    {
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var seed = Ctx(connection))
        {
            await new WellKnownMaskSeeder(seed, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        using var read = Ctx(connection);
        return await MaskContainmentRules.LoadAsync(read, _tenantId, CancellationToken.None);
    }

    [Fact]
    public async Task The_model_refuses_exactly_what_the_static_rules_refused()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rules = await SeededRulesAsync(connection);

        var mismatches = new List<string>();
        var allowedCount = 0;
        var refusedCount = 0;

        foreach (var child in WellKnownMaskIds.All)
        {
            // The no-parent case is not a curiosity: a repository created before it is stamped, and a folder
            // mid-heal, both look exactly like it.
            foreach (var parent in WellKnownMaskIds.All.Select(p => (Guid?)p).Append(null))
            {
                var byRules = StaticVerdict(child, parent);

                bool byModel;
                try
                {
                    rules.Verify("x", child, parent);
                    byModel = true;
                }
                catch (TypedFolderContainmentException)
                {
                    byModel = false;
                }

                if (byModel)
                {
                    allowedCount++;
                }
                else
                {
                    refusedCount++;
                }

                if (byRules != byModel)
                {
                    mismatches.Add(
                        $"child {Name(child)} under parent {(parent is { } p ? Name(p) : "<none>")}: "
                        + $"static says {byRules}, model says {byModel}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));

        // An equivalence test between two agreeing functions is also passed by two functions that agree on
        // "always allow". So the space must contain both answers, or the loop above proves nothing at all.
        Assert.True(allowedCount > 0 && refusedCount > 0,
            $"the sweep saw {allowedCount} admitted and {refusedCount} refused — it must see both.");
    }

    [Fact]
    public async Task A_tenant_whose_rules_never_reached_the_database_is_the_permissive_case()
    {
        // The failure mode that matters, stated as a test rather than as a comment: rules read from the model
        // are only as good as the seed, and an unseeded — or unhealed — tenant reads as "anything goes". This
        // asserts that shape exists and is recognisable, so the heal is understood as load-bearing rather than
        // as tidying. WellKnownMaskSeeder runs at every startup for every tenant precisely because of this.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var read = Ctx(connection);
        var empty = await MaskContainmentRules.LoadAsync(read, _tenantId, CancellationToken.None);

        // A Contact at the archive root — refused by a seeded tenant, permitted by one with no rules at all.
        empty.Verify("orphan.vcf", WellKnownMaskIds.Contact, null);
    }

    // How EnforceTypedFolderContainmentAsync read the STATIC tables before ADR 0655, cardinality excluded —
    // that stays static and is a different question (capacity, not admission).
    private static bool StaticVerdict(Guid childMaskId, Guid? parentMaskId)
    {
        var alsoTakesPlainFolders =
            WellKnownMaskIds.AlsoAdmitPlainFolders.Any(m => m.FolderMaskId == parentMaskId)
            && childMaskId == WellKnownMaskIds.Folder;

        if (!alsoTakesPlainFolders
            && WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == parentMaskId) is { } parentRule
            && !parentRule.Admits.Any(a => a.MaskId == childMaskId))
        {
            return false;
        }

        if (WellKnownMaskIds.AdmittingFolders.TryGetValue(childMaskId, out var admitting)
            && !admitting.Any(r => r.FolderMaskId == parentMaskId))
        {
            return false;
        }

        return !(WellKnownMaskIds.FolderMasks.Contains(childMaskId)
                 && WellKnownMaskIds.NoSubfolderMasks.Any(m => m.FolderMaskId == parentMaskId));
    }

    private static string Name(Guid maskId) =>
        typeof(WellKnownMaskIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Guid) && (Guid)f.GetValue(null)! == maskId)
            .Select(f => f.Name)
            .FirstOrDefault() ?? maskId.ToString();
}
