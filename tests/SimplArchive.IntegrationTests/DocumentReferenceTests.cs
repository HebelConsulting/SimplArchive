using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// See ADR "Desktop drag-and-drop move and reference": the new CanMove right and the DocumentReference
// (shortcut) entity's DB-level invariants.
public class DocumentReferenceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    [Fact]
    public async Task CanMove_flows_through_effective_rights()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seed.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, UserId = userId, CanSee = true, CanMove = true, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, tenantId);
        var rights = await new EffectiveRightsCalculator(context).GetEffectiveRightsAsync(userId, documentId);

        Assert.True(rights.CanMove);
        Assert.False(rights.CanCreateSubItems);
    }

    [Fact]
    public async Task Rejects_a_self_reference()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using var context = CreateContext(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = folderId, TenantId = tenantId, Name = "Folder", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        context.DocumentReferences.Add(new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentFolderId = folderId,
            TargetDocumentId = folderId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // CK_DocumentReferences_NotSelf rejects TargetDocumentId == ParentFolderId.
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_duplicate_reference_in_the_same_folder()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using var context = CreateContext(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = folderId, TenantId = tenantId, Name = "Folder", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = targetId, TenantId = tenantId, Name = "Target", ParentId = folderId, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        context.DocumentReferences.Add(new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentFolderId = folderId,
            TargetDocumentId = targetId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        context.DocumentReferences.Add(new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentFolderId = folderId,
            TargetDocumentId = targetId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // Unique (TenantId, ParentFolderId, TargetDocumentId) rejects a duplicate shortcut.
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
