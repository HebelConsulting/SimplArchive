using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class TenantAdminAclBypassTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task A_tenant_admin_has_every_right_on_a_document_with_no_acl_entries_at_all()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = adminUserId, TenantId = tenantId, Email = "admin@example.com", DisplayName = "Admin", IsTenantAdmin = true, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = adminUserId, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(adminUserId, documentId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanReadContent);
        Assert.True(rights.CanEditContent);
        Assert.True(rights.CanEditIndexData);
        Assert.True(rights.CanDelete);
        Assert.True(rights.CanCreateSubItems);
        Assert.True(rights.CanManagePermissions);
    }

    [Fact]
    public async Task A_non_admin_user_with_no_acl_entries_has_no_rights()
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
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "user@example.com", DisplayName = "User", IsTenantAdmin = false, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanReadContent);
        Assert.False(rights.CanManagePermissions);
    }
}
