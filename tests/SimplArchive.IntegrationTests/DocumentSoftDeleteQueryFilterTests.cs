using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Document is the first entity implementing both ITenantScoped and ISoftDeletable — see ADR "Document
// delete/restore (recycle bin) implementation". EF Core only combines multiple query filters on the same
// entity with AND when every filter is *named*; mixing an anonymous and a named filter throws at
// model-build time, and two anonymous filters silently drop the first one (a real tenant-isolation leak).
// This exercises the real combination live against SQLite, not just the throwaway repro used to discover it.
public class DocumentSoftDeleteQueryFilterTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task Tenant_filter_and_soft_delete_filter_combine_with_and()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var activeInA = Guid.NewGuid();
        var deletedInA = Guid.NewGuid();
        var activeInB = Guid.NewGuid();
        var serviceAccountA = Guid.NewGuid();
        var serviceAccountB = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.AddRange(
                new Tenant { Id = tenantA, Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow },
                new Tenant { Id = tenantB, Name = "Tenant B", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.AddRange(
                new ServiceAccount { Id = serviceAccountA, TenantId = tenantA, Name = "SA-A", OpenIddictApplicationClientId = "sa-a", CreatedAt = DateTimeOffset.UtcNow },
                new ServiceAccount { Id = serviceAccountB, TenantId = tenantB, Name = "SA-B", OpenIddictApplicationClientId = "sa-b", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.AddRange(
                new Document { Id = activeInA, TenantId = tenantA, Name = "Active-A", CreatedByServiceAccountId = serviceAccountA, CreatedAt = DateTimeOffset.UtcNow },
                new Document { Id = deletedInA, TenantId = tenantA, Name = "Deleted-A", DeletedAt = DateTimeOffset.UtcNow, CreatedByServiceAccountId = serviceAccountA, CreatedAt = DateTimeOffset.UtcNow },
                new Document { Id = activeInB, TenantId = tenantB, Name = "Active-B", CreatedByServiceAccountId = serviceAccountB, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var readContext = CreateContext(connection, tenantA);

        // Both filters active: only the non-deleted document belonging to tenant A.
        var defaultView = await readContext.Documents.Select(d => d.Id).ToListAsync();
        Assert.Equal([activeInA], defaultView);

        // Soft-delete filter lifted, tenant filter still enforced: both tenant A documents, never tenant B's.
        var recycleBinView = await readContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Select(d => d.Id)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { activeInA, deletedInA }.OrderBy(id => id), recycleBinView);
    }
}
