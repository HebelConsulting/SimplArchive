using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class AclEntryDocumentScopeTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private record Fixture(Guid TenantId, Guid RootDocumentId, Guid UserId, Guid DocumentId);

    private static async Task<Fixture> SeedDocumentAsync(SqliteConnection connection, bool breaksInheritance = true)
    {
        var tenantId = Guid.NewGuid();
        var rootDocumentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using var seedContext = CreateContext(connection);
        seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Documents.Add(new Document { Id = rootDocumentId, TenantId = tenantId, Name = "AP", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Documents.Add(new Document { Id = documentId, TenantId = tenantId, ParentId = rootDocumentId, Name = "Invoices", BreaksInheritance = breaksInheritance, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await seedContext.SaveChangesAsync();

        return new Fixture(tenantId, rootDocumentId, userId, documentId);
    }

    [Fact]
    public async Task Allows_a_document_scoped_grant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedDocumentAsync(connection);

        using var context = CreateContext(connection, fixture.TenantId);
        context.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = fixture.TenantId, DocumentId = fixture.DocumentId, UserId = fixture.UserId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task Rejects_a_duplicate_document_scoped_grant_for_the_same_user()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedDocumentAsync(connection);

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = fixture.TenantId, DocumentId = fixture.DocumentId, UserId = fixture.UserId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        context.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = fixture.TenantId, DocumentId = fixture.DocumentId, UserId = fixture.UserId, CanEditContent = true, CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // "The repository" is now just the root Document (ParentId == null) — see ADR "Repository/Document
    // unification". The same user can hold independent grants on both the root and one of its descendants.
    [Fact]
    public async Task Allows_the_same_user_granted_on_both_the_root_document_and_one_of_its_descendants()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedDocumentAsync(connection);

        using var context = CreateContext(connection, fixture.TenantId);
        context.AclEntries.AddRange(
            new AclEntry { Id = Guid.NewGuid(), TenantId = fixture.TenantId, DocumentId = fixture.RootDocumentId, UserId = fixture.UserId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow },
            new AclEntry { Id = Guid.NewGuid(), TenantId = fixture.TenantId, DocumentId = fixture.DocumentId, UserId = fixture.UserId, CanDelete = true, CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(2, affected);
    }

    [Fact]
    public async Task Deleting_a_document_cascades_to_its_own_acl_entries()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedDocumentAsync(connection);
        var aclEntryId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.AclEntries.Add(new AclEntry { Id = aclEntryId, TenantId = fixture.TenantId, DocumentId = fixture.DocumentId, UserId = fixture.UserId, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using (var deleteContext = CreateContext(connection, fixture.TenantId))
        {
            var document = await deleteContext.Documents.SingleAsync(d => d.Id == fixture.DocumentId);
            deleteContext.Documents.Remove(document);
            await deleteContext.SaveChangesAsync();
        }

        using var verifyContext = CreateContext(connection, fixture.TenantId);
        var stillExists = await verifyContext.AclEntries.AnyAsync(a => a.Id == aclEntryId);

        Assert.False(stillExists);
    }
}
