using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// "The repository" is now just the root Document (ParentId == null) — see ADR "Repository/Document
// unification".
public class EffectiveRightsServiceAccountTests
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
        var serviceAccountId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "CI Uploader", OpenIddictApplicationClientId = "svc-ci-uploader", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByServiceAccountId = serviceAccountId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, ServiceAccountId = serviceAccountId, CanReadContent = true, CanEditContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.True(rights.CanReadContent);
        Assert.True(rights.CanEditContent);
        Assert.False(rights.CanSee);
        Assert.False(rights.CanDelete);
    }

    [Fact]
    public async Task Returns_no_rights_when_no_grant_exists()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "CI Uploader", OpenIddictApplicationClientId = "svc-ci-uploader", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByServiceAccountId = serviceAccountId, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanReadContent);
        Assert.False(rights.CanEditContent);
        Assert.False(rights.CanEditIndexData);
        Assert.False(rights.CanDelete);
        Assert.False(rights.CanCreateSubItems);
        Assert.False(rights.CanManagePermissions);
    }

    [Fact]
    public async Task A_grant_targeting_a_group_never_leaks_into_a_service_accounts_rights()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "CI Uploader", OpenIddictApplicationClientId = "svc-ci-uploader", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByServiceAccountId = serviceAccountId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "Everyone", CreatedAt = DateTimeOffset.UtcNow });
            // Deliberately not a ServiceAccount grant, to prove a Group-targeted entry can't leak in.
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, GroupId = groupId, CanSee = true, CanDelete = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanDelete);
    }

    [Fact]
    public async Task A_deactivated_service_account_gets_no_rights_despite_a_direct_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "CI Uploader", OpenIddictApplicationClientId = "svc-ci-uploader", IsActive = false, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByServiceAccountId = serviceAccountId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, ServiceAccountId = serviceAccountId, CanSee = true, CanEditContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanEditContent);
    }

    [Fact]
    public async Task A_deactivated_tenant_leaves_a_service_account_with_no_rights()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", Status = TenantStatus.Deactivated, CreatedAt = DateTimeOffset.UtcNow, DeactivatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "CI Uploader", OpenIddictApplicationClientId = "svc-ci-uploader", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "AP", CreatedByServiceAccountId = serviceAccountId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, ServiceAccountId = serviceAccountId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.False(rights.CanSee);
    }
}
