using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class EffectiveRightsDeactivatedTenantTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task A_user_in_a_deactivated_tenant_gets_no_rights_despite_a_direct_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Tenant",
                Status = TenantStatus.Deactivated,
                DeactivatedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, UserId = userId, CanSee = true, CanEditContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanEditContent);
    }

    [Fact]
    public async Task A_tenant_admin_in_a_deactivated_tenant_gets_no_rights_either()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Tenant",
                Status = TenantStatus.Deactivated,
                DeactivatedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.Users.Add(new User { Id = adminUserId, TenantId = tenantId, Email = "admin@example.com", DisplayName = "Admin", IsActive = true, IsTenantAdmin = true, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = adminUserId, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(adminUserId, documentId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanManagePermissions);
    }

    [Fact]
    public async Task A_user_in_an_active_tenant_still_gets_their_rights()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, UserId = userId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsAsync(userId, documentId);

        Assert.True(rights.CanSee);
    }
}
