using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// See ADR "Document tags": the DocumentTag entity's DB-level invariants — the (TenantId, DocumentId, Tag)
// unique index and the tenant query filter.
public class DocumentTagTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    [Fact]
    public async Task Round_trips_and_rejects_a_duplicate_tag_on_a_document()
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
            seed.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Doc", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            seed.DocumentTags.Add(new DocumentTag { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, Tag = "urgent", CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        // A second document may carry the same tag; the same document may not (unique per document).
        using (var context = CreateContext(connection, tenantId))
        {
            Assert.Equal(["urgent"], await context.DocumentTags.Where(t => t.DocumentId == documentId).Select(t => t.Tag).ToListAsync());

            context.DocumentTags.Add(new DocumentTag { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, Tag = "urgent", CreatedAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Is_isolated_by_the_tenant_query_filter()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var docA = Guid.NewGuid();
        var docB = Guid.NewGuid();

        using (var seed = CreateContext(connection))
        {
            foreach (var (tenant, doc, tag) in new[] { (tenantA, docA, "alpha"), (tenantB, docB, "beta") })
            {
                var userId = Guid.NewGuid();
                seed.Tenants.Add(new Tenant { Id = tenant, Name = $"T-{tenant:N}", CreatedAt = DateTimeOffset.UtcNow });
                seed.Users.Add(new User { Id = userId, TenantId = tenant, Email = $"u-{tenant:N}@example.com", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
                seed.Documents.Add(new Document { Id = doc, TenantId = tenant, Name = "Doc", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
                seed.DocumentTags.Add(new DocumentTag { Id = Guid.NewGuid(), TenantId = tenant, DocumentId = doc, Tag = tag, CreatedAt = DateTimeOffset.UtcNow });
            }
            await seed.SaveChangesAsync();
        }

        using var scoped = CreateContext(connection, tenantA);
        var tags = await scoped.DocumentTags.Select(t => t.Tag).ToListAsync();
        Assert.Equal(["alpha"], tags); // tenant B's "beta" is filtered out
    }
}
