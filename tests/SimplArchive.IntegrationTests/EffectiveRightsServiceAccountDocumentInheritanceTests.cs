using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// "The repository" is now just the root Document (ParentId == null) — see ADR "Repository/Document
// unification".
public class EffectiveRightsServiceAccountDocumentInheritanceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task A_document_that_never_overrides_falls_back_to_the_root_documents_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "SA", OpenIddictApplicationClientId = "sa-client", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = folderId, TenantId = tenantId, Name = "Folder", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, ParentId = folderId, Name = "Invoice.pdf", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = folderId, ServiceAccountId = serviceAccountId, CanSee = true, CanReadContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanReadContent);
    }

    [Fact]
    public async Task A_document_that_breaks_inheritance_ignores_the_root_documents_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "SA", OpenIddictApplicationClientId = "sa-client", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, ParentId = rootId, Name = "Confidential.pdf", BreaksInheritance = true, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            // Root grants CanSee — should NOT apply, since the document overrides.
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = rootId, ServiceAccountId = serviceAccountId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.False(rights.CanSee);
    }

    [Fact]
    public async Task A_document_that_breaks_inheritance_uses_its_own_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "SA", OpenIddictApplicationClientId = "sa-client", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Confidential.pdf", BreaksInheritance = true, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, ServiceAccountId = serviceAccountId, CanDelete = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.True(rights.CanDelete);
        Assert.False(rights.CanSee);
    }

    [Fact]
    public async Task A_deeply_nested_document_inherits_from_the_nearest_ancestor_that_breaks_inheritance()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();
        var rootFolderId = Guid.NewGuid();
        var overrideFolderId = Guid.NewGuid();
        var leafDocumentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "SA", OpenIddictApplicationClientId = "sa-client", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = rootFolderId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = overrideFolderId, TenantId = tenantId, ParentId = rootFolderId, Name = "HR", BreaksInheritance = true, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = leafDocumentId, TenantId = tenantId, ParentId = overrideFolderId, Name = "Salary.pdf", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            // Root-level grant should NOT apply — the "HR" ancestor overrides.
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = rootFolderId, ServiceAccountId = serviceAccountId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            // "HR" folder's own override grant should apply to the nested leaf.
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = overrideFolderId, ServiceAccountId = serviceAccountId, CanReadContent = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, leafDocumentId);

        Assert.True(rights.CanReadContent);
        Assert.False(rights.CanSee);
    }

    [Fact]
    public async Task A_deactivated_service_account_gets_no_rights_despite_an_overriding_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var serviceAccountId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.ServiceAccounts.Add(new ServiceAccount { Id = serviceAccountId, TenantId = tenantId, Name = "SA", OpenIddictApplicationClientId = "sa-client", IsActive = false, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Confidential.pdf", BreaksInheritance = true, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, ServiceAccountId = serviceAccountId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var calculator = new EffectiveRightsCalculator(context);

        var rights = await calculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId);

        Assert.False(rights.CanSee);
    }
}
