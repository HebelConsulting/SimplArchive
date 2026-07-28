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

// Merge-into-existing import (ADR "Merge-into-existing import"): importing an archive into a target folder with
// merge on overlays the two trees — an archive folder whose name matches an existing folder under the target is
// reused (its documents land alongside the existing ones) rather than creating a duplicate folder.
public class RepositoryMergeImportTests
{
    private sealed class DictStorage : IObjectStorageClient
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Objects[objectKey]));
        public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            Objects[objectKey] = ms.ToArray();
            return Task.CompletedTask;
        }
        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(Objects.ContainsKey(objectKey));
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult((long)(Objects.TryGetValue(objectKey, out var __b) ? __b.Length : 0));
        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StorageObject>>([]);
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    [Fact]
    public async Task Merge_overlays_a_same_named_folder_instead_of_duplicating_it()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var storage = new DictStorage();

        // ---- Tenant A: a "Shared" folder (export root) containing DocA (v1) ----
        accessor.TenantId = _tenantA;
        Guid sharedAId;
        using (var db = Ctx(connection, accessor))
        {
            var userId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = _tenantA, Name = "A", CreatedAt = DateTimeOffset.UtcNow });
            db.Tenants.Add(new Tenant { Id = _tenantB, Name = "B", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = _tenantA, Email = "jane@a.test", DisplayName = "Jane", CreatedAt = DateTimeOffset.UtcNow });
            var shared = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, Name = "Shared", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            var docA = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, ParentId = shared.Id, Name = "DocA", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(shared, docA);
            sharedAId = shared.Id;
            var bytes = Encoding.UTF8.GetBytes("a-bytes");
            storage.Objects["a/v1.txt"] = bytes;
            db.DocumentVersions.Add(new DocumentVersion { Id = Guid.NewGuid(), TenantId = _tenantA, DocumentId = docA.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = Convert.ToHexString(SHA256.HashData(bytes)), ObjectKey = "a/v1.txt", DocumentDate = new DateOnly(2025, 1, 1), CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var zip = new MemoryStream();
        using (var db = Ctx(connection, accessor))
        {
            await new RepositoryExporter(db, storage).ExportAsync(sharedAId, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.All, null), false, zip, CancellationToken.None);
        }
        zip.Position = 0;

        // ---- Tenant B: a "Dest" repository already containing a "Shared" folder with an existing document ----
        accessor.TenantId = _tenantB;
        Guid destId, sharedBId;
        using (var db = Ctx(connection, accessor))
        {
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, TenantId = _tenantB, Email = "bob@b.test", DisplayName = "Bob", CreatedAt = DateTimeOffset.UtcNow });
            var dest = new Document { Id = Guid.NewGuid(), TenantId = _tenantB, Name = "Dest", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            var sharedB = new Document { Id = Guid.NewGuid(), TenantId = _tenantB, ParentId = dest.Id, Name = "Shared", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            var existing = new Document { Id = Guid.NewGuid(), TenantId = _tenantB, ParentId = sharedB.Id, Name = "Existing", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(dest, sharedB, existing);
            destId = dest.Id;
            sharedBId = sharedB.Id;
            await db.SaveChangesAsync();
        }

        // ---- Merge the archive into "Dest" ----
        using (var db = Ctx(connection, accessor))
        {
            await new RepositoryImporter(db, storage, accessor, new WellKnownMaskSeeder(db), new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance))
                .ImportAsync(zip, destId, updateExisting: false, includePermissions: false, merge: true, SimplArchive.Api.Documents.LeafMergeMode.Rename, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            // Dest still has exactly one "Shared" folder — the pre-existing one was reused, not duplicated.
            var sharedChildren = await db.Documents.Where(d => d.ParentId == destId && d.Name == "Shared").Select(d => d.Id).ToListAsync();
            Assert.Equal([sharedBId], sharedChildren);

            // That folder now contains both the original document and the merged-in one.
            var names = await db.Documents.Where(d => d.ParentId == sharedBId).Select(d => d.Name).ToListAsync();
            Assert.Equal(new HashSet<string> { "Existing", "DocA" }, names.ToHashSet());
        }
    }

    // Leaf-document merge modes (ADR "Leaf-document merge modes"): when merging and a same-named DOCUMENT already
    // exists, NewVersion appends the incoming versions onto it, Skip drops the incoming one, and Rename (default)
    // creates a renamed copy.
    [Theory]
    [InlineData(LeafMergeMode.NewVersion, 1, 2)] // one DocA, its version count grew to 2 (appended)
    [InlineData(LeafMergeMode.Skip, 1, 1)]       // one DocA, untouched (incoming dropped)
    [InlineData(LeafMergeMode.Rename, 2, 1)]     // a second, renamed DocA copy; the existing one untouched
    public async Task Leaf_merge_mode_handles_a_same_named_document(LeafMergeMode mode, int expectedDocaCount, int expectedExistingVersions)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();
        var storage = new DictStorage();

        // Tenant A: "Shared" / "DocA" (v1, content "a-v1").
        accessor.TenantId = _tenantA;
        Guid sharedAId;
        using (var db = Ctx(connection, accessor))
        {
            var uid = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = _tenantA, Name = "A", CreatedAt = DateTimeOffset.UtcNow });
            db.Tenants.Add(new Tenant { Id = _tenantB, Name = "B", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = uid, TenantId = _tenantA, Email = "jane@a.test", DisplayName = "Jane", CreatedAt = DateTimeOffset.UtcNow });
            var shared = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, Name = "Shared", CreatedByUserId = uid, CreatedAt = DateTimeOffset.UtcNow };
            var docA = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, ParentId = shared.Id, Name = "DocA", CreatedByUserId = uid, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(shared, docA);
            sharedAId = shared.Id;
            var bytes = Encoding.UTF8.GetBytes("a-v1");
            storage.Objects["a/v1.txt"] = bytes;
            db.DocumentVersions.Add(new DocumentVersion { Id = Guid.NewGuid(), TenantId = _tenantA, DocumentId = docA.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = Convert.ToHexString(SHA256.HashData(bytes)), ObjectKey = "a/v1.txt", DocumentDate = new DateOnly(2025, 1, 1), CreatedByUserId = uid, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var zip = new MemoryStream();
        using (var db = Ctx(connection, accessor))
        {
            await new RepositoryExporter(db, storage).ExportAsync(sharedAId, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.All, null), false, zip, CancellationToken.None);
        }
        zip.Position = 0;

        // Tenant B: "Dest" / "Shared" / "DocA" already exists (v1, different content "b-existing").
        accessor.TenantId = _tenantB;
        Guid destId, existingDocaId;
        using (var db = Ctx(connection, accessor))
        {
            var uid = Guid.NewGuid();
            db.Users.Add(new User { Id = uid, TenantId = _tenantB, Email = "bob@b.test", DisplayName = "Bob", CreatedAt = DateTimeOffset.UtcNow });
            var dest = new Document { Id = Guid.NewGuid(), TenantId = _tenantB, Name = "Dest", CreatedByUserId = uid, CreatedAt = DateTimeOffset.UtcNow };
            var sharedB = new Document { Id = Guid.NewGuid(), TenantId = _tenantB, ParentId = dest.Id, Name = "Shared", CreatedByUserId = uid, CreatedAt = DateTimeOffset.UtcNow };
            var docA = new Document { Id = Guid.NewGuid(), TenantId = _tenantB, ParentId = sharedB.Id, Name = "DocA", CreatedByUserId = uid, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(dest, sharedB, docA);
            destId = dest.Id;
            existingDocaId = docA.Id;
            var bytes = Encoding.UTF8.GetBytes("b-existing");
            db.DocumentVersions.Add(new DocumentVersion { Id = Guid.NewGuid(), TenantId = _tenantB, DocumentId = docA.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = Convert.ToHexString(SHA256.HashData(bytes)), ObjectKey = "b/existing.txt", DocumentDate = new DateOnly(2025, 1, 1), CreatedByUserId = uid, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            await new RepositoryImporter(db, storage, accessor, new WellKnownMaskSeeder(db), new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance))
                .ImportAsync(zip, destId, updateExisting: false, includePermissions: false, merge: true, mode, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            var sharedBId = await db.Documents.Where(d => d.ParentId == destId && d.Name == "Shared").Select(d => d.Id).SingleAsync();
            var docaCount = await db.Documents.CountAsync(d => d.ParentId == sharedBId && d.Name.StartsWith("DocA"));
            Assert.Equal(expectedDocaCount, docaCount);

            var existingVersions = await db.DocumentVersions.CountAsync(v => v.DocumentId == existingDocaId);
            Assert.Equal(expectedExistingVersions, existingVersions);

            if (mode == LeafMergeMode.NewVersion)
            {
                // The appended version follows the existing one (number 2) and carries the incoming content.
                var v2 = await db.DocumentVersions.SingleAsync(v => v.DocumentId == existingDocaId && v.VersionNumber == 2);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("a-v1"))), v2.Sha256Hash);
            }
        }
    }
}
