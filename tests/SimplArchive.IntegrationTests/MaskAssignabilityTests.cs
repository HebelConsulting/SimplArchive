using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A mask says how it can be assigned (#671): whether it types a FOLDER, and which file extensions claim it.
// Before this the answer existed only as static app knowledge and scattered literals, so the picker offered
// masks the containment rules would refuse and the only way to find out was to save and read the error (#580).
//
// Two halves, and the upgrade half is the one that fails quietly: every mask that already exists was created
// before these columns did, so it reads "not a folder mask" with no extensions until the HEAL corrects it. A
// fresh-volume test sees nothing wrong, because every tenant it creates is new — the trap #664 recorded.
public class MaskAssignabilityTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<SqliteConnection> TenantAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        return connection;
    }

    private async Task SeedAsync(SqliteConnection connection)
    {
        using var db = Ctx(connection);
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance)
            .EnsureWellKnownMasksAsync(_tenantId);
    }

    [Fact]
    public async Task A_fresh_tenant_gets_the_folder_partition_and_the_extensions()
    {
        using var connection = await TenantAsync();
        await SeedAsync(connection);

        using var db = Ctx(connection);
        var masks = await db.Masks.IgnoreQueryFilters(["TenantFilter"])
            .Where(m => m.TenantId == _tenantId).ToDictionaryAsync(m => m.Id, m => m.IsFolderMask);

        // Every folder mask says so, and — the half that makes it meaningful — no item mask does. A flag that
        // is true of everything answers nothing.
        Assert.All(WellKnownMaskIds.FolderMasks, id => Assert.True(masks[id], $"{id} is a folder mask and did not say so."));
        Assert.All(WellKnownMaskIds.ItemMasks, id => Assert.False(masks[id], $"{id} is an item mask and claimed to be a folder mask."));

        var extensions = await db.MaskFileExtensions.IgnoreQueryFilters(["TenantFilter"])
            .Where(e => e.TenantId == _tenantId)
            .ToDictionaryAsync(e => e.Extension, e => e.MaskId);

        Assert.Equal(WellKnownMaskIds.EMail, extensions[".eml"]);
        Assert.Equal(WellKnownMaskIds.EMail, extensions[".msg"]);
        Assert.Equal(WellKnownMaskIds.Contact, extensions[".vcf"]);
        Assert.Equal(WellKnownMaskIds.Appointment, extensions[".ics"]);

        // Nothing else claims an extension. Basic Entry in particular must not, or every upload would be
        // classified as one and the freely-assignable mask would stop being a choice.
        Assert.Equal(4, extensions.Count);
    }

    [Fact]
    public async Task A_tenant_seeded_before_the_columns_existed_is_healed()
    {
        using var connection = await TenantAsync();
        await SeedAsync(connection);

        // Wind the tenant back to what an upgrade actually finds: masks that exist, with the column at its
        // default and not one extension row. Seeding again must correct BOTH.
        using (var stale = Ctx(connection))
        {
            foreach (var mask in await stale.Masks.IgnoreQueryFilters(["TenantFilter"]).Where(m => m.TenantId == _tenantId).ToListAsync())
            {
                mask.IsFolderMask = false;
            }

            stale.MaskFileExtensions.RemoveRange(
                await stale.MaskFileExtensions.IgnoreQueryFilters(["TenantFilter"]).Where(e => e.TenantId == _tenantId).ToListAsync());
            await stale.SaveChangesAsync();
        }

        await SeedAsync(connection);

        using var db = Ctx(connection);
        var folder = await db.Masks.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(m => m.TenantId == _tenantId && m.Id == WellKnownMaskIds.Addressbook);
        Assert.True(folder.IsFolderMask, "the heal left an existing folder mask unmarked.");

        Assert.Equal(4, await db.MaskFileExtensions.IgnoreQueryFilters(["TenantFilter"])
            .CountAsync(e => e.TenantId == _tenantId));
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_an_extension()
    {
        using var connection = await TenantAsync();
        await SeedAsync(connection);
        await SeedAsync(connection);

        using var db = Ctx(connection);

        // Idempotence matters here more than usual: the seeder runs on EVERY startup for every tenant, so a
        // seed that appended would violate the unique index on the second boot rather than the hundredth.
        Assert.Equal(4, await db.MaskFileExtensions.IgnoreQueryFilters(["TenantFilter"])
            .CountAsync(e => e.TenantId == _tenantId));
    }

    [Fact]
    public async Task An_extension_belongs_to_at_most_one_mask_per_tenant()
    {
        using var connection = await TenantAsync();
        await SeedAsync(connection);

        using var db = Ctx(connection);
        db.MaskFileExtensions.Add(new MaskFileExtension
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            MaskId = WellKnownMaskIds.BasicEntry,
            Extension = ".eml",
        });

        // The constraint IS the design: with two masks claiming .eml, the picker and the classifier could
        // disagree and automatic classification would need a tie-break that does not exist. Making the
        // ambiguity unrepresentable is what removes the question — and it is what stopped Note from being
        // mapped to .eml alongside eMail, since a note is told from a mail by WHERE it is filed.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
