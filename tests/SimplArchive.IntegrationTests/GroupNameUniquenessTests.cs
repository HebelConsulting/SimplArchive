using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class GroupNameUniquenessTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task Rejects_two_root_level_groups_with_the_same_name()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.Add(new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Finance", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.Groups.Add(new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Finance", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_two_sibling_groups_under_the_same_parent_with_the_same_name()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.AddRange(
                new Group { Id = parentId, TenantId = tenantId, Name = "Parent", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
                new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Archive", ParentGroupId = parentId, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        context.Groups.Add(new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Archive", ParentGroupId = parentId, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_the_same_name_under_different_parents()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var parentAId = Guid.NewGuid();
        var parentBId = Guid.NewGuid();

        using var context = CreateContext(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Groups.AddRange(
            new Group { Id = parentAId, TenantId = tenantId, Name = "Parent A", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
            new Group { Id = parentBId, TenantId = tenantId, Name = "Parent B", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
            new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Archive", ParentGroupId = parentAId, CreatedAt = DateTimeOffset.UtcNow },
            new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Archive", ParentGroupId = parentBId, CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(5, affected); // Tenant + 2 parents + 2 "Archive" siblings under different parents
    }
}
