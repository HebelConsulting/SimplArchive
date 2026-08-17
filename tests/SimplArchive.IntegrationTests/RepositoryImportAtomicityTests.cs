using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// An import is one unit of work (ADR 0614): it either lands whole or leaves nothing. The importer needs several
// SaveChanges to do its job — a parent must exist before its child can be parented onto it — and before the
// transaction each of those was a point where a failure could leave half an archive filed with no record of what
// was missing. These tests fail a real import midway and assert the destination tenant is untouched.
public class RepositoryImportAtomicityTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private static RepositoryImporter Importer(SimplArchiveDbContext db, IObjectStorageClient storage, CurrentTenantAccessor accessor) =>
        new(db, storage, accessor, new WellKnownMaskSeeder(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Masks.WellKnownMaskSeeder>.Instance),
            new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance),
            NoOpDocumentIndexQueue.Instance, NoOpSearchablePdfQueue.Instance,
            new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance));

    // Exports a two-leaf repository from tenant A and returns the archive. Two leaves is the point: the importer
    // uploads their blobs one after another, so failing the second means the first document is already written.
    private static async Task<MemoryStream> SeedAndExportAsync(SqliteConnection connection, CurrentTenantAccessor accessor, Guid tenantA, Guid tenantB, InMemoryObjectStorage storage)
    {
        accessor.TenantId = tenantA;
        Guid srcId;
        using (var db = Ctx(connection, accessor))
        {
            var userId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantA, Name = "A", CreatedAt = DateTimeOffset.UtcNow });
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "B", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantA, Email = "jane@a.test", DisplayName = "Jane", CreatedAt = DateTimeOffset.UtcNow });

            var src = new Document { Id = Guid.NewGuid(), TenantId = tenantA, Name = "Src", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            db.Add(src);
            srcId = src.Id;

            foreach (var name in new[] { "DocA", "DocB" })
            {
                var doc = new Document { Id = Guid.NewGuid(), TenantId = tenantA, ParentId = src.Id, Name = name, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
                db.Add(doc);
                var payload = Encoding.UTF8.GetBytes($"contents of {name}");
                var key = $"a/{name}.txt";
                storage.Objects[key] = payload;
                db.DocumentVersions.Add(new DocumentVersion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantA,
                    DocumentId = doc.Id,
                    Status = DocumentVersionStatus.Confirmed,
                    VersionNumber = 1,
                    Sha256Hash = Convert.ToHexString(SHA256.HashData(payload)),
                    ObjectKey = key,
                    CreatedByUserId = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        var zip = new MemoryStream();
        using (var db = Ctx(connection, accessor))
        {
            await new RepositoryExporter(db, storage).ExportAsync(srcId, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.All, null), false, zip, CancellationToken.None);
        }

        zip.Position = 0;
        return zip;
    }

    [Fact]
    public async Task A_failure_midway_leaves_the_destination_tenant_untouched()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var storage = new InMemoryObjectStorage();
        var zip = await SeedAndExportAsync(connection, accessor, tenantA, tenantB, storage);

        // Fail the SECOND blob upload: by then the first document and version are written, the placeholder author
        // exists, and the well-known masks are seeded — everything the rollback has to take back with it.
        storage.FailOnPut = 2;
        accessor.TenantId = tenantB;
        using (var db = Ctx(connection, accessor))
        {
            await Assert.ThrowsAsync<IOException>(() => Importer(db, storage, accessor)
                .ImportAsync(zip, null, updateExisting: false, includePermissions: false, merge: false, LeafMergeMode.Rename, CancellationToken.None));
        }

        // Nothing of the import survives in B — not the tree, not the versions, not the placeholder principal the
        // first phase created, and not a single byte on the storage counter.
        using (var db = Ctx(connection, accessor))
        {
            Assert.Empty(await db.Documents.IgnoreQueryFilters().Where(d => d.TenantId == tenantB).ToListAsync());
            Assert.Empty(await db.DocumentVersions.IgnoreQueryFilters().Where(v => v.TenantId == tenantB).ToListAsync());
            Assert.Empty(await db.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenantB).ToListAsync());
            Assert.Equal(0, (await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantB)).StorageUsedBytes);
        }

        // Tenant A — the source — is untouched either way: the rollback is scoped to the import's own writes.
        accessor.TenantId = tenantA;
        using (var db = Ctx(connection, accessor))
        {
            Assert.Equal(3, await db.Documents.CountAsync());
        }
    }

    [Fact]
    public async Task A_retry_after_a_failed_import_succeeds_whole()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var storage = new InMemoryObjectStorage();
        var zip = await SeedAndExportAsync(connection, accessor, tenantA, tenantB, storage);

        storage.FailOnPut = 2;
        accessor.TenantId = tenantB;
        using (var db = Ctx(connection, accessor))
        {
            await Assert.ThrowsAsync<IOException>(() => Importer(db, storage, accessor)
                .ImportAsync(zip, null, updateExisting: false, includePermissions: false, merge: false, LeafMergeMode.Rename, CancellationToken.None));
        }

        // The operator fixes the storage and imports the same archive again. Because the failed attempt left
        // nothing behind, this is a first import rather than a repair: the whole tree arrives, with no duplicate
        // and no leftover to merge against.
        storage.FailOnPut = 0;
        zip.Position = 0;
        RepositoryImporter.ImportResult result;
        using (var db = Ctx(connection, accessor))
        {
            result = await Importer(db, storage, accessor)
                .ImportAsync(zip, null, updateExisting: false, includePermissions: false, merge: false, LeafMergeMode.Rename, CancellationToken.None);
        }

        Assert.Equal("Src", result.RootName);
        using (var db = Ctx(connection, accessor))
        {
            Assert.Equal(3, await db.Documents.CountAsync());
            Assert.Equal(2, await db.DocumentVersions.CountAsync());
            Assert.Single(await db.Documents.Where(d => d.Name == "DocA").ToListAsync());
        }
    }
}
