using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class TenantIsolationTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor accessor)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, accessor);
    }

    [Fact]
    public async Task Queries_only_return_rows_for_the_current_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var accessor = new CurrentTenantAccessor();
        using (var setupContext = CreateContext(connection, accessor))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantA = new Tenant { Id = Guid.NewGuid(), Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow };
        var tenantB = new Tenant { Id = Guid.NewGuid(), Name = "Tenant B", CreatedAt = DateTimeOffset.UtcNow };

        using (var seedContext = CreateContext(connection, accessor))
        {
            seedContext.Tenants.AddRange(tenantA, tenantB);
            seedContext.Users.AddRange(
                new User { Id = Guid.NewGuid(), TenantId = tenantA.Id, Email = "a@example.com", DisplayName = "User A", CreatedAt = DateTimeOffset.UtcNow },
                new User { Id = Guid.NewGuid(), TenantId = tenantB.Id, Email = "b@example.com", DisplayName = "User B", CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var readContext = CreateContext(connection, accessor);
        accessor.TenantId = tenantA.Id;

        var visibleUsers = await readContext.Users.ToListAsync();

        Assert.Single(visibleUsers);
        Assert.Equal("a@example.com", visibleUsers[0].Email);
    }
}
