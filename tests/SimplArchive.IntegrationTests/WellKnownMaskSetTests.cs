using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// WellKnownMaskIds.All must agree with what is actually seeded, in BOTH directions.
//
// This exists because a second, hand-maintained copy of the list caused a real defect:
// RepositoryExporter.IsWellKnown named three of the eleven well-known masks and was never updated as the
// other eight arrived. Export therefore marked a Note, Contact or Appointment as NOT well-known — and the
// importer creates a FRESH mask for anything not well-known, so imported documents wore a duplicate mask with
// a different id. Every WellKnownMaskIds check then stopped recognising them: typed-folder containment, the
// IMAP projection, the clients' type column.
//
// `All` is now derived by reflection from the declarations, so it cannot fall behind them. What reflection
// CANNOT catch is the other drift — an id declared but never seeded, or seeded but never declared — which is
// what this test is for.
public class WellKnownMaskSetTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    [Fact]
    public async Task Every_declared_well_known_mask_is_seeded_and_vice_versa()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        using (var db = Ctx(connection))
        {
            // The masks hang off a tenant, so it has to exist before they can be seeded.
            db.Tenants.Add(new SimplArchive.Domain.Tenants.Tenant
            {
                Id = _tenantId,
                Name = "Acme",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        using (var db = Ctx(connection))
        {
            var seeded = await db.Masks.Select(m => m.Id).ToListAsync();

            // Declared but not seeded: the id exists, so code compiles and compares against it, but no tenant
            // ever has that mask — every check against it silently never matches.
            Assert.Empty(WellKnownMaskIds.All.Except(seeded));

            // Seeded but not declared: the mask exists in every tenant yet `All` does not know it, so the
            // exporter would mark it not-well-known and the importer would duplicate it — precisely the bug
            // this set was introduced to end.
            Assert.Empty(seeded.Except(WellKnownMaskIds.All));
        }
    }

    [Fact]
    public void The_set_is_derived_and_covers_the_masks_added_after_the_stale_list_was_written()
    {
        // Named explicitly rather than counted: a count assertion passes for the wrong reasons the moment a
        // mask is added and another removed, and these eight are exactly the ones the stale hand-written list
        // in the exporter had missed.
        foreach (var id in new[]
                 {
                     WellKnownMaskIds.UserFolder, WellKnownMaskIds.Notebook, WellKnownMaskIds.Note,
                     WellKnownMaskIds.NotebookSection, WellKnownMaskIds.Addressbook, WellKnownMaskIds.Calendar,
                     WellKnownMaskIds.Contact, WellKnownMaskIds.Appointment,
                 })
        {
            Assert.Contains(id, WellKnownMaskIds.All);
        }

        // And the three the old list did have, so the fix is additive rather than a replacement that lost them.
        Assert.Contains(WellKnownMaskIds.BasicEntry, WellKnownMaskIds.All);
        Assert.Contains(WellKnownMaskIds.Folder, WellKnownMaskIds.All);
        Assert.Contains(WellKnownMaskIds.EMail, WellKnownMaskIds.All);
    }
}
