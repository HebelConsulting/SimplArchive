using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// "A repository" is now just a root Document (ParentId == null) — see ADR "Repository/Document
// unification" — so these tests grant against a root Document directly.
public class EffectiveRightsCalculatorTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task Includes_rights_from_a_direct_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, UserId = userId, CanSee = true, CanReadContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanReadContent);
        Assert.False(rights.CanEditContent);
    }

    [Fact]
    public async Task Includes_rights_granted_to_a_group_the_user_directly_belongs_to()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "Finance", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, UserId = userId, GroupId = groupId });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, GroupId = groupId, CanEditContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.True(rights.CanEditContent);
        Assert.False(rights.CanSee);
    }

    [Fact]
    public async Task Includes_rights_granted_to_a_descendant_of_a_group_the_user_belongs_to()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var engineeringGroupId = Guid.NewGuid();
        var backendGroupId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "manager@example.com", DisplayName = "Manager", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.AddRange(
                new Group { Id = engineeringGroupId, TenantId = tenantId, Name = "Engineering", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
                new Group { Id = backendGroupId, TenantId = tenantId, Name = "Backend", ParentGroupId = engineeringGroupId, CreatedAt = DateTimeOffset.UtcNow });
            // The user is only a direct member of the parent "Engineering" group.
            seedContext.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, UserId = userId, GroupId = engineeringGroupId });
            // The grant is on the child "Backend" group.
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, GroupId = backendGroupId, CanDelete = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.True(rights.CanDelete);
    }

    [Fact]
    public async Task Does_not_grant_a_childs_membership_the_parent_groups_rights()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var engineeringGroupId = Guid.NewGuid();
        var backendGroupId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "dev@example.com", DisplayName = "Dev", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.AddRange(
                new Group { Id = engineeringGroupId, TenantId = tenantId, Name = "Engineering", ParentGroupId = null, CreatedAt = DateTimeOffset.UtcNow },
                new Group { Id = backendGroupId, TenantId = tenantId, Name = "Backend", ParentGroupId = engineeringGroupId, CreatedAt = DateTimeOffset.UtcNow });
            // The user is only a direct member of the CHILD "Backend" group.
            seedContext.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, UserId = userId, GroupId = backendGroupId });
            // The grant is on the PARENT "Engineering" group.
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, GroupId = engineeringGroupId, CanDelete = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.False(rights.CanDelete);
    }

    [Fact]
    public async Task Unions_rights_across_multiple_matching_grants()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "Finance", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, UserId = userId, GroupId = groupId });
            seedContext.AclEntries.AddRange(
                new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, UserId = userId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow },
                new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, GroupId = groupId, CanEditContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanEditContent);
        Assert.False(rights.CanDelete);
    }
}
