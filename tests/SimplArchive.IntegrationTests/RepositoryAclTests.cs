using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// ACL in export/import (ADR "ACL in export/import"): with the opt-in on, a document's own AclEntry grants — to a
// user and to a group — round-trip into a fresh tenant (the group matched-or-placeholdered by name); with the
// import toggle off, the archived grants are ignored.
public class RepositoryAclTests
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    // Seeds tenant A (repo with BreaksInheritance + a user grant + a group grant) and returns the export bytes.
    private async Task<MemoryStream> SeedAndExportAsync(SqliteConnection c, CurrentTenantAccessor accessor, InMemoryObjectStorage storage, Guid tenantB)
    {
        accessor.TenantId = _tenantA;
        Guid repoId;
        using (var db = Ctx(c, accessor))
        {
            var userId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = _tenantA, Name = "A", CreatedAt = DateTimeOffset.UtcNow });
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "B", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = _tenantA, Email = "jane@a.test", DisplayName = "Jane", CreatedAt = DateTimeOffset.UtcNow });
            var group = new Group { Id = Guid.NewGuid(), TenantId = _tenantA, Name = "Editors", CreatedAt = DateTimeOffset.UtcNow };
            db.Groups.Add(group);
            db.GroupMemberships.Add(new GroupMembership { TenantId = _tenantA, GroupId = group.Id, UserId = userId }); // jane ∈ Editors

            var repo = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, Name = "Repo", BreaksInheritance = true, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            db.Documents.Add(repo);
            repoId = repo.Id;

            db.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = _tenantA, DocumentId = repo.Id, UserId = userId, CanSee = true, CanReadContent = true, CreatedAt = DateTimeOffset.UtcNow });
            db.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = _tenantA, DocumentId = repo.Id, GroupId = group.Id, CanSee = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var zip = new MemoryStream();
        using (var db = Ctx(c, accessor))
        {
            await new RepositoryExporter(db, storage).ExportAsync(repoId, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.All, null), includePermissions: true, zip, CancellationToken.None);
        }
        zip.Position = 0;
        return zip;
    }

    [Fact]
    public async Task Acl_round_trips_with_permissions_on()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantB = Guid.NewGuid();
        var storage = new InMemoryObjectStorage();
        var zip = await SeedAndExportAsync(connection, accessor, storage, tenantB);

        accessor.TenantId = tenantB;
        RepositoryImporter.ImportResult result;
        using (var db = Ctx(connection, accessor))
        {
            result = await new RepositoryImporter(db, storage, accessor, new WellKnownMaskSeeder(db), new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance), NoOpDocumentIndexQueue.Instance, NoOpSearchablePdfQueue.Instance, new SimplArchive.Api.Documents.PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance)).ImportAsync(zip, null, updateExisting: false, includePermissions: true, merge: false, SimplArchive.Api.Documents.LeafMergeMode.Rename, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            var root = await db.Documents.SingleAsync(d => d.Id == result.RootDocumentId);
            Assert.True(root.BreaksInheritance);

            var acl = await db.AclEntries.Where(a => a.DocumentId == root.Id).ToListAsync();
            Assert.Equal(2, acl.Count);

            // The user grant → the matched/placeholdered user (jane), with its rights.
            var jane = await db.Users.SingleAsync(u => u.NormalizedEmail == "JANE@A.TEST");
            var userGrant = Assert.Single(acl, a => a.UserId == jane.Id);
            Assert.True(userGrant.CanSee && userGrant.CanReadContent);
            Assert.False(userGrant.CanEditContent);

            // The group grant → a placeholder "Editors" group created in tenant B.
            var editors = await db.Groups.SingleAsync(g => g.Name == "Editors");
            var groupGrant = Assert.Single(acl, a => a.GroupId == editors.Id);
            Assert.True(groupGrant.CanSee);

            // The membership edge (jane ∈ Editors) also round-tripped (ADR "Group memberships in export").
            Assert.True(await db.GroupMemberships.AnyAsync(m => m.GroupId == editors.Id && m.UserId == jane.Id));
        }
    }

    [Fact]
    public async Task Import_ignores_acl_when_permissions_off()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantB = Guid.NewGuid();
        var storage = new InMemoryObjectStorage();
        var zip = await SeedAndExportAsync(connection, accessor, storage, tenantB);

        accessor.TenantId = tenantB;
        using (var db = Ctx(connection, accessor))
        {
            var result = await new RepositoryImporter(db, storage, accessor, new WellKnownMaskSeeder(db), new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance), NoOpDocumentIndexQueue.Instance, NoOpSearchablePdfQueue.Instance, new SimplArchive.Api.Documents.PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance)).ImportAsync(zip, null, updateExisting: false, includePermissions: false, merge: false, SimplArchive.Api.Documents.LeafMergeMode.Rename, CancellationToken.None);
            Assert.Empty(await db.AclEntries.Where(a => a.DocumentId == result.RootDocumentId).ToListAsync());
            Assert.Empty(await db.Groups.Where(g => g.Name == "Editors").ToListAsync()); // no placeholder group either
        }
    }
}
