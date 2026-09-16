using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Provisioning;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Documents;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The founding tenant administrator must hold EVERY system right, and this asks the question by REFLECTION so
// that a right added later cannot quietly miss the list.
//
// WHY A RIGHT MISSING HERE IS WORSE THAN "NOT HELD". Nothing is implied by IsTenantAdmin — the resolver carries
// it as one flag among the others, not as a bypass — and SystemRightsPolicy caps a grant to what the caller
// already holds. So a right absent from provisioning is held by NOBODY and grantable by nobody: permanently
// un-grantable, in every tenant the service ever provisions. The only ways in are a direct database write or a
// PlatformAdministrator path, neither of which is the administration surface.
//
// That is not hypothetical. CanBlockResources and CanReleaseResources arrived with the inventory-booking work
// and this list was never extended (#1241). Measured on the v0.26.0 demo: both tenant admins had them false, so
// a grounded aircraft could not be released by anybody through the app — ADR 0778 derives suspension from an
// active block and the only way out is clearing it, which needs CanReleaseResources.
//
// WHY IT WENT UNNOTICED FOR FOUR RELEASES is the part worth guarding: the failure is entirely silent. Nothing
// throws, no endpoint 500s, no test fails. A feature simply cannot be used, and the affordance for it is hidden
// by the same rights check — so it looks like the feature was never built rather than like a defect.
//
// The list is taken from SystemRightsSet rather than typed out here, because a hand-written list is the thing
// that failed: it is only ever as current as the last person who remembered to extend it. Add a right to that
// record and this test fails until provisioning grants it.
public class ProvisionedAdminHoldsEverySystemRightTests
{
    [Fact]
    public async Task The_founding_administrator_holds_every_right_SystemRightsSet_knows_about()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setup = Ctx(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        using var db = Ctx(connection);
        var provisioned = await ServiceFor(db).ProvisionAsync(
            "Acme", "admin@acme.test", "Acme Admin", "Acme Repository", "Sup3rSecret!");

        var admin = await db.Users.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(u => u.TenantId == provisioned.TenantId && u.IsTenantAdmin);

        // Every component of SystemRightsSet, by name, read off the User the service just created.
        var rights = typeof(SystemRightsSet).GetProperties()
            .Where(p => p.PropertyType == typeof(bool))
            .Select(p => p.Name)
            .ToList();

        // Anti-vacuous: if reflection stopped finding the rights, every assertion below would pass on an empty
        // set. Nineteen at the time of writing; the bound is deliberately loose so adding one does not fail here
        // instead of where it matters.
        Assert.True(rights.Count >= 18,
            $"Only {rights.Count} boolean rights found on SystemRightsSet — the reflection stopped seeing them, "
            + "which would make this test pass while checking nothing.");

        var missing = rights
            .Where(name => typeof(User).GetProperty(name) is { } p && p.GetValue(admin) is false)
            .ToList();

        Assert.True(missing.Count == 0,
            "The founding tenant administrator does NOT hold these system rights:\n"
            + string.Join("\n", missing.Select(m => $"  {m}"))
            + "\n\nA right missing from TenantProvisioningService is not merely un-held — because nothing is "
            + "implied by IsTenantAdmin and a caller may only grant what it holds, it is PERMANENTLY "
            + "UN-GRANTABLE by anyone in every tenant this provisions (#1241). Add it to the provisioning "
            + "block; do not relax this assertion.");

        // And the reflection must actually be reaching User, not silently skipping names it cannot resolve —
        // a typo'd or renamed property would otherwise make `missing` empty for the wrong reason.
        var unresolved = rights.Where(name => typeof(User).GetProperty(name) is null).ToList();
        Assert.True(unresolved.Count == 0,
            "These SystemRightsSet components have no matching property on User, so they were never checked:\n"
            + string.Join("\n", unresolved.Select(u => $"  {u}")));
    }

    private static TenantProvisioningService ServiceFor(SimplArchiveDbContext db) =>
        new(db,
            new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance),
            new SensitivityLabelSeeder(db),
            new InMemoryObjectStorage(),
            new SimplArchive.Api.Documents.PersonalRepositoryProvisioner(db, new NoOpAuditRecorder()));

    private static SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor());
}
