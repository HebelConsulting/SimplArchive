using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

public class DocumentParentIntegrityTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private record Fixture(Guid TenantId, Guid UserId);

    private static async Task<Fixture> SeedTenantAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using var seedContext = CreateContext(connection);
        seedContext.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seedContext.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        await seedContext.SaveChangesAsync();

        return new Fixture(tenantId, userId);
    }

    private static Document NewDocument(Fixture fixture, Guid id, string name, Guid? parentId = null)
        => new()
        {
            Id = id,
            TenantId = fixture.TenantId,
            ParentId = parentId,
            Name = name,
            CreatedByUserId = fixture.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task Rejects_a_document_that_would_become_its_own_ancestor()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedTenantAsync(connection);
        var folderId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.Documents.Add(NewDocument(fixture, folderId, "Folder"));
            seedContext.Documents.Add(NewDocument(fixture, childId, "Child", parentId: folderId));
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        var folder = await context.Documents.SingleAsync(d => d.Id == folderId);
        folder.ParentId = childId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_document_pointing_to_a_parent_in_a_different_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedTenantAsync(connection);
        var otherTenantId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var parentId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Users.Add(new User { Id = otherUserId, TenantId = otherTenantId, Email = "b@example.com", DisplayName = "B", CreatedAt = DateTimeOffset.UtcNow });
            seedContext.Documents.Add(new Document
            {
                Id = parentId,
                TenantId = otherTenantId,
                Name = "Parent",
                CreatedByUserId = otherUserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        context.Documents.Add(NewDocument(fixture, Guid.NewGuid(), "Child", parentId: parentId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    // Root-level sibling-name uniqueness is tenant-wide (TenantId + ParentId == null) — this is now what
    // used to be Repository.Name's own separate tenant-wide uniqueness (ADR 0154), reproduced by this one
    // simpler check now that "a repository" is just a root Document — see ADR "Repository/Document
    // unification".
    [Fact]
    public async Task Rejects_duplicate_sibling_names_at_root_level()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedTenantAsync(connection);

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.Documents.Add(NewDocument(fixture, Guid.NewGuid(), "Invoices"));
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        context.Documents.Add(NewDocument(fixture, Guid.NewGuid(), "Invoices"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_duplicate_sibling_names_under_the_same_parent()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedTenantAsync(connection);
        var folderId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.Documents.Add(NewDocument(fixture, folderId, "Folder"));
            seedContext.Documents.Add(NewDocument(fixture, Guid.NewGuid(), "Invoice.pdf", parentId: folderId));
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        context.Documents.Add(NewDocument(fixture, Guid.NewGuid(), "Invoice.pdf", parentId: folderId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_the_same_name_under_different_parents()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setupContext = CreateContext(connection)) await setupContext.Database.EnsureCreatedAsync();

        var fixture = await SeedTenantAsync(connection);
        var folderAId = Guid.NewGuid();
        var folderBId = Guid.NewGuid();

        using (var seedContext = CreateContext(connection, fixture.TenantId))
        {
            seedContext.Documents.Add(NewDocument(fixture, folderAId, "2024"));
            seedContext.Documents.Add(NewDocument(fixture, folderBId, "2025"));
            seedContext.Documents.Add(NewDocument(fixture, Guid.NewGuid(), "Invoice.pdf", parentId: folderAId));
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection, fixture.TenantId);
        context.Documents.Add(NewDocument(fixture, Guid.NewGuid(), "Invoice.pdf", parentId: folderBId));

        var affected = await context.SaveChangesAsync();

        Assert.Equal(1, affected);
    }
}
