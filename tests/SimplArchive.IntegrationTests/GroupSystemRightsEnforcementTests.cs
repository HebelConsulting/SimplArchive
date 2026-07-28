using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// ADR "Enforce group system rights for members": a user's effective system rights are their own unioned
// with every group they effectively belong to (direct + descendants, membership flowing down). A group's
// IsTenantAdmin also confers the full ACL bypass on its members.
public class GroupSystemRightsEnforcementTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    [Fact]
    public async Task A_user_gains_a_management_right_held_by_a_group_they_are_a_member_of()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            // The user holds nothing directly.
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "u@example.com", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
            // The group grants CanManageMasks.
            seed.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "Mask managers", CanManageMasks = true, CreatedAt = DateTimeOffset.UtcNow });
            seed.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, GroupId = groupId, UserId = userId });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var rights = await new UserSystemRightsResolver(context).GetEffectiveSystemRightsAsync(userId);

        Assert.True(rights.CanManageMasks);      // via the group
        Assert.False(rights.CanManageUsers);     // not held anywhere
        Assert.False(rights.IsTenantAdmin);
    }

    [Fact]
    public async Task Rights_flow_down_from_a_parent_group_a_user_is_in_to_its_descendants()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var parentGroupId = Guid.NewGuid();
        var childGroupId = Guid.NewGuid();

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "u@example.com", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
            seed.Groups.Add(new Group { Id = parentGroupId, TenantId = tenantId, Name = "Parent", CreatedAt = DateTimeOffset.UtcNow });
            // The descendant carries the right; membership in the parent flows down to it.
            seed.Groups.Add(new Group { Id = childGroupId, TenantId = tenantId, Name = "Child", ParentGroupId = parentGroupId, CanManageRepositories = true, CreatedAt = DateTimeOffset.UtcNow });
            seed.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, GroupId = parentGroupId, UserId = userId });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var rights = await new UserSystemRightsResolver(context).GetEffectiveSystemRightsAsync(userId);

        Assert.True(rights.CanManageRepositories); // from the descendant group
    }

    [Fact]
    public async Task Effective_rights_union_own_and_group_rights()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "u@example.com", DisplayName = "U", CanManageUsers = true, CreatedAt = DateTimeOffset.UtcNow });
            seed.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "G", CanManageMasks = true, CreatedAt = DateTimeOffset.UtcNow });
            seed.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, GroupId = groupId, UserId = userId });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var rights = await new UserSystemRightsResolver(context).GetEffectiveSystemRightsAsync(userId);

        Assert.True(rights.CanManageUsers); // own
        Assert.True(rights.CanManageMasks); // via the group
    }

    [Fact]
    public async Task A_group_flagged_tenant_admin_confers_the_full_acl_bypass_on_its_members()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "u@example.com", DisplayName = "U", IsTenantAdmin = false, CreatedAt = DateTimeOffset.UtcNow });
            seed.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "Administrators", IsTenantAdmin = true, CreatedAt = DateTimeOffset.UtcNow });
            seed.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, GroupId = groupId, UserId = userId });
            // A document with no AclEntry at all — only an (effective) tenant admin sees it.
            seed.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Secret", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);
        Assert.True(rights.CanSee);
        Assert.True(rights.CanReadContent);
        Assert.True(rights.CanManagePermissions);

        var searchAccess = await calculator.GetSearchAccessForUserAsync(userId);
        Assert.True(searchAccess.BypassAcl);
    }
}
