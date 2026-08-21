using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The fifth fact reaching the database (#678) — on a fresh tenant AND on one that already existed.
//
// The heal matters more than the seed here, and differently from the icon's. The migration backfills every
// existing mask to TRUE, because a tenant-authored mask should be creatable; that is the right default and the
// wrong answer for the six the application provisions. So between the migration and the heal, an upgraded
// tenant briefly has "New Repository" as a legal answer — the heal is what closes it, in the same startup, and
// a test that only covered a fresh tenant would never see that.
public class MaskUserCreatableSeedTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task SeedAsync(SqliteConnection connection)
    {
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var seed = Ctx(connection);
        await new WellKnownMaskSeeder(seed, NullLogger<WellKnownMaskSeeder>.Instance)
            .EnsureWellKnownMasksAsync(_tenantId);
    }

    private async Task<Dictionary<Guid, bool>> CreatableAsync(SqliteConnection connection)
    {
        using var read = Ctx(connection);
        return await read.Masks.ToDictionaryAsync(m => m.Id, m => m.UserCreatable);
    }

    [Fact]
    public async Task The_six_the_application_provisions_are_closed_and_everything_else_is_open()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        var creatable = await CreatableAsync(connection);

        foreach (var maskId in WellKnownMaskIds.All)
        {
            var expected = !WellKnownMaskIds.NotUserCreatable.Contains(maskId);
            Assert.Equal(expected, creatable[maskId]);
        }

        // Named explicitly as well as compared to the table, so the table itself is checked against intent
        // rather than only against itself — the failure mode where a test restates the code it is testing.
        Assert.False(creatable[WellKnownMaskIds.Notebook]);
        Assert.False(creatable[WellKnownMaskIds.Mailbox]);
        Assert.False(creatable[WellKnownMaskIds.Repository]);
        Assert.True(creatable[WellKnownMaskIds.Addressbook]);
        Assert.True(creatable[WellKnownMaskIds.Calendar]);
        Assert.True(creatable[WellKnownMaskIds.Folder]);
    }

    // A tenant that existed before the column did. The migration backfills TRUE, so this reproduces exactly
    // what an upgrade leaves behind — including the six that must not stay that way.
    [Fact]
    public async Task An_upgraded_tenant_has_its_provisioned_masks_closed_again()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        using (var backfill = Ctx(connection))
        {
            foreach (var mask in await backfill.Masks.ToListAsync())
            {
                mask.UserCreatable = true;
            }

            await backfill.SaveChangesAsync();
        }

        Assert.All(await CreatableAsync(connection), pair => Assert.True(pair.Value));

        using (var heal = Ctx(connection))
        {
            await new WellKnownMaskSeeder(heal, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        var creatable = await CreatableAsync(connection);
        foreach (var maskId in WellKnownMaskIds.NotUserCreatable)
        {
            Assert.False(creatable[maskId], $"{maskId} stayed creatable after the heal.");
        }
    }

    // A TENANT-authored mask is not the seeder's business and must survive it untouched — otherwise the
    // default that makes this useful (creatable unless said otherwise) would be overwritten on every startup.
    [Fact]
    public async Task A_tenant_authored_mask_is_creatable_and_the_seeder_leaves_it_alone()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        var ownMaskId = Guid.NewGuid();
        using (var add = Ctx(connection))
        {
            add.Masks.Add(new Mask { Id = ownMaskId, TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow, IsFolderMask = true });
            add.MaskVersions.Add(new MaskVersion { Id = Guid.NewGuid(), TenantId = _tenantId, MaskId = ownMaskId, Name = "Case File", CreatedAt = DateTimeOffset.UtcNow });
            await add.SaveChangesAsync();
        }

        // The CLR default did the work — nothing set this, which is what "creatable unless said otherwise" means.
        Assert.True((await CreatableAsync(connection))[ownMaskId]);

        using (var heal = Ctx(connection))
        {
            await new WellKnownMaskSeeder(heal, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        Assert.True((await CreatableAsync(connection))[ownMaskId]);
    }

    // The round trip that matters for a column whose default is TRUE: the value that is NOT the default has to
    // survive an INSERT. An UPDATE always sends the real value, so this is the direction that hides — the whole
    // shape of the store-default trap recorded for this repo.
    //
    // HONEST ABOUT ITS REACH: this does not currently reproduce that trap. Adding HasDefaultValue(true) to the
    // model was tried, with and without the CLR initializer, and false still round-trips — EF Core 10 derives
    // the sentinel from the configured default rather than from the CLR default. So this pins the BEHAVIOUR
    // (false is storable) rather than guarding one mechanism, and it would catch a future EF change, a wrong
    // HasSentinel, or someone replacing the initializer with something that reintroduces the old shape.
    [Fact]
    public async Task A_mask_inserted_as_not_creatable_stays_not_creatable()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        var closedMaskId = Guid.NewGuid();
        using (var add = Ctx(connection))
        {
            add.Masks.Add(new Mask
            {
                Id = closedMaskId,
                TenantId = _tenantId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsFolderMask = true,
                UserCreatable = false,
            });
            await add.SaveChangesAsync();
        }

        Assert.False((await CreatableAsync(connection))[closedMaskId]);
    }
}
