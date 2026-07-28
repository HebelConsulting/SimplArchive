using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class GroupCycleDetectionTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor());
    }

    [Fact]
    public async Task Rejects_a_group_set_as_its_own_parent()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "Self", ParentGroupId = groupId, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_transitive_cycle_introduced_by_re_parenting()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var grandparentId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.AddRange(
                new Group { Id = grandparentId, TenantId = tenantId, Name = "Grandparent", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
                new Group { Id = parentId, TenantId = tenantId, Name = "Parent", ParentGroupId = grandparentId, CreatedAt = DateTimeOffset.UtcNow },
                new Group { Id = childId, TenantId = tenantId, Name = "Child", ParentGroupId = parentId, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        // Re-parent the grandparent underneath its own grandchild — a transitive cycle.
        var accessor = new CurrentTenantAccessor { TenantId = tenantId };
        using var scopedContext = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, accessor);

        var grandparent = await scopedContext.Groups.SingleAsync(g => g.Id == grandparentId);
        grandparent.ParentGroupId = childId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => scopedContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_parent_group_belonging_to_a_different_tenant_already_persisted()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var otherTenantsGroupId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.AddRange(
                new Tenant { Id = tenantAId, Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow },
                new Tenant { Id = tenantBId, Name = "Tenant B", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.Add(new Group { Id = otherTenantsGroupId, TenantId = tenantBId, Name = "Tenant B Group", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        var accessor = new CurrentTenantAccessor { TenantId = tenantAId };
        using var context = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, accessor);

        context.Groups.Add(new Group { Id = Guid.NewGuid(), TenantId = tenantAId, Name = "Tenant A Group", ParentGroupId = otherTenantsGroupId, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_parent_group_belonging_to_a_different_tenant_in_the_same_batch()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var tenantBGroupId = Guid.NewGuid();

        using var context = CreateContext(connection);
        context.Tenants.AddRange(
            new Tenant { Id = tenantAId, Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow },
            new Tenant { Id = tenantBId, Name = "Tenant B", CreatedAt = DateTimeOffset.UtcNow });
        context.Groups.AddRange(
            new Group { Id = tenantBGroupId, TenantId = tenantBId, Name = "Tenant B Group", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
            new Group { Id = Guid.NewGuid(), TenantId = tenantAId, Name = "Tenant A Group", ParentGroupId = tenantBGroupId, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_a_valid_deeply_nested_hierarchy()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Groups.AddRange(
            new Group { Id = rootId, TenantId = tenantId, Name = "Root", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
            new Group { Id = childId, TenantId = tenantId, Name = "Child", ParentGroupId = rootId, CreatedAt = DateTimeOffset.UtcNow },
            new Group { Id = grandchildId, TenantId = tenantId, Name = "Grandchild", ParentGroupId = childId, CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(4, affected); // Tenant + 3 groups
    }
}
