using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Mask.Icon reaching the database — on a fresh tenant AND on one that already existed.
//
// The heal is the half worth testing. A seed that only ever GROWS leaves every pre-existing tenant behind, and
// a fresh-volume test cannot see it because the only tenants it creates are new ones — which is exactly how
// #574 shipped a maskless Notes folder to everyone who already had an account. So the second test below
// deliberately blanks the column first and re-runs the seeder, standing in for a tenant whose rows predate it.
public class MaskIconSeedTests
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

    private async Task<Dictionary<Guid, string?>> IconsAsync(SqliteConnection connection)
    {
        using var read = Ctx(connection);
        return await read.Masks.ToDictionaryAsync(m => m.Id, m => m.Icon);
    }

    [Fact]
    public async Task A_fresh_tenant_gets_every_shipped_token()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        var icons = await IconsAsync(connection);

        foreach (var (maskId, token) in WellKnownMaskIds.IconTokens)
        {
            Assert.Equal(token, icons[maskId]);
        }
    }

    // The generic shapes carry NO token, on purpose: they are the folder and the document the fallback already
    // draws. Asserted so the absence reads as a decision rather than as three masks somebody forgot — and so
    // that a later change giving them one has to argue for it here.
    [Theory]
    [InlineData("Folder")]
    [InlineData("MyDocuments")]
    [InlineData("BasicEntry")]
    public async Task The_generic_shapes_stay_on_the_default(string mask)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        var maskId = mask switch
        {
            "Folder" => WellKnownMaskIds.Folder,
            "MyDocuments" => WellKnownMaskIds.MyDocuments,
            _ => WellKnownMaskIds.BasicEntry,
        };

        Assert.Null((await IconsAsync(connection))[maskId]);
    }

    // A tenant that existed before the column did. Blanking every Icon reproduces exactly the state a real
    // upgrade leaves behind — the migration adds a nullable column and fills nothing.
    [Fact]
    public async Task A_tenant_that_predates_the_column_is_healed()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        using (var blank = Ctx(connection))
        {
            foreach (var mask in await blank.Masks.ToListAsync())
            {
                mask.Icon = null;
            }

            await blank.SaveChangesAsync();
        }

        Assert.All(await IconsAsync(connection), pair => Assert.Null(pair.Value));

        using (var heal = Ctx(connection))
        {
            await new WellKnownMaskSeeder(heal, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        var icons = await IconsAsync(connection);
        foreach (var (maskId, token) in WellKnownMaskIds.IconTokens)
        {
            Assert.Equal(token, icons[maskId]);
        }
    }

    // The well-known set is the authority for the masks the application ships, so a wrong value is CORRECTED
    // rather than left alone. Without this, a token changed in a release would apply to new tenants only and
    // the two would drift apart silently — the same trap as the grow-only seed, one step further along.
    [Fact]
    public async Task A_wrong_token_on_a_shipped_mask_is_corrected()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection);

        using (var tamper = Ctx(connection))
        {
            var calendar = await tamper.Masks.SingleAsync(m => m.Id == WellKnownMaskIds.Calendar);
            calendar.Icon = "something-else";
            await tamper.SaveChangesAsync();
        }

        using (var heal = Ctx(connection))
        {
            await new WellKnownMaskSeeder(heal, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(_tenantId);
        }

        Assert.Equal("calendar", (await IconsAsync(connection))[WellKnownMaskIds.Calendar]);
    }
}
