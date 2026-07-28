using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.LegalHolds;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.LegalHolds;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Verifies the legal-hold freeze rule (ADR "Legal hold & retention enforcement"): a document is frozen if it —
// or any ancestor — is in an ACTIVE hold; a released hold no longer freezes; AnyDirectlyHeldAsync reports the
// directly-held documents (used by the delete-subtree check).
public class LegalHoldServiceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    [Fact]
    public async Task Freeze_follows_the_ancestor_chain_and_respects_release()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "u@acme.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        var root = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Repo", CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        var folder = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, ParentId = root.Id, Name = "Folder", CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        var child = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, ParentId = folder.Id, Name = "Child", CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        var unrelated = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, ParentId = root.Id, Name = "Other", CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };

        var hold = new LegalHold { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Matter A", PlacedByUserId = user.Id, PlacedAt = DateTimeOffset.UtcNow };
        var holdItem = new LegalHoldItem { Id = Guid.NewGuid(), TenantId = tenant.Id, LegalHoldId = hold.Id, DocumentId = folder.Id, CreatedAt = DateTimeOffset.UtcNow };

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Documents.AddRange(root, folder, child, unrelated);
            seed.LegalHolds.Add(hold);
            seed.LegalHoldItems.Add(holdItem);
            await seed.SaveChangesAsync();
        }

        tenantAccessor.TenantId = tenant.Id;

        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            var service = new LegalHoldService(ctx);

            // The held folder + its descendant are frozen; the folder's ancestor (root) and an unrelated
            // sibling are not.
            Assert.True(await service.IsFrozenAsync(folder.Id));
            Assert.True(await service.IsFrozenAsync(child.Id));
            Assert.False(await service.IsFrozenAsync(root.Id));
            Assert.False(await service.IsFrozenAsync(unrelated.Id));

            // AnyDirectlyHeldAsync only reports the directly-held document.
            Assert.True(await service.AnyDirectlyHeldAsync(new[] { folder.Id }));
            Assert.False(await service.AnyDirectlyHeldAsync(new[] { child.Id, unrelated.Id }));
        }

        // Releasing the hold unfreezes everything.
        using (var release = CreateContext(connection, tenantAccessor))
        {
            var h = await release.LegalHolds.SingleAsync(x => x.Id == hold.Id);
            h.ReleasedAt = DateTimeOffset.UtcNow;
            await release.SaveChangesAsync();
        }

        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            var service = new LegalHoldService(ctx);
            Assert.False(await service.IsFrozenAsync(folder.Id));
            Assert.False(await service.IsFrozenAsync(child.Id));
            Assert.False(await service.AnyDirectlyHeldAsync(new[] { folder.Id }));
        }
    }
}
