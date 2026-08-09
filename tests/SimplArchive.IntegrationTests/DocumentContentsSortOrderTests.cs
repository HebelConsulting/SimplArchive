using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Every FolderContentsSortOrder value survives a round-trip (ADR "Per-folder contents sort order").
//
// Name did not. The column carried HasDefaultValue(DocumentDate), and EF omits a property from the INSERT when it
// equals its sentinel — by default the CLR default, which for this enum is Name (0). So a folder created with
// Name had no value sent, the store default won, and it came back as DocumentDate: the one value the type could
// not express. Only on INSERT — an UPDATE always sends the real value, which is why setting the order in the UI
// on an existing folder worked and hid it.
//
// EF's own warning named this exactly ("configured with a database-generated default, but has no configured
// sentinel"), and it was in every startup log. The fix was to drop the store default, since its only job —
// backfilling existing folders — was done by the migration that introduced it.
//
// Theory over the whole enum rather than a Name-only case: the next value added to it gets this coverage free,
// and a sentinel mistake on a different value would be the same silent shape.
public class DocumentContentsSortOrderTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    [Theory]
    [InlineData(FolderContentsSortOrder.Name)]
    [InlineData(FolderContentsSortOrder.DocumentDate)]
    [InlineData(FolderContentsSortOrder.Created)]
    public async Task A_folder_keeps_the_sort_order_it_was_created_with(FolderContentsSortOrder order)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "u@acme.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Folder",
            CreatedByUserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ContentsSortOrder = order,
        };

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Documents.Add(folder);
            await seed.SaveChangesAsync();
        }

        // A fresh context, so this is what the database holds and not the tracked instance.
        tenantAccessor.TenantId = tenant.Id;
        using var read = CreateContext(connection, tenantAccessor);
        var stored = await read.Documents.SingleAsync(d => d.Id == folder.Id);

        Assert.Equal(order, stored.ContentsSortOrder);
    }
}
