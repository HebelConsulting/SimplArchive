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

// Idempotent re-import (ADR "Idempotent re-import"): re-importing the same archive matches previously-imported
// documents by their origin key instead of duplicating them — a no-op with updateExisting=false, and a
// version-adding sync with updateExisting=true.
public class RepositoryReimportTests
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
    public async Task Re_import_is_idempotent_and_update_syncs_new_versions()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var storage = new DictStorage();
        var userId = Guid.NewGuid();
        Guid srcId, docAId;

        // ---- Seed tenant A: Repo → DocA (v1) ----
        accessor.TenantId = _tenantA;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantA, Name = "A", CreatedAt = DateTimeOffset.UtcNow });
            db.Tenants.Add(new Tenant { Id = _tenantB, Name = "B", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = _tenantA, Email = "jane@a.test", DisplayName = "Jane", CreatedAt = DateTimeOffset.UtcNow });
            var repo = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, Name = "Repo", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            var docA = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, ParentId = repo.Id, Name = "DocA", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(repo, docA);
            srcId = repo.Id;
            docAId = docA.Id;
            AddVersion(db, storage, _tenantA, docA.Id, 1, "v1-bytes", "a/v1.txt", userId);
            await db.SaveChangesAsync();
        }

        // ---- Import #1 into B as a new repository ----
        accessor.TenantId = _tenantB;
        var first = await ImportAsync(connection, storage, accessor, await ExportAsync(connection, storage, accessor, _tenantA, srcId), false);
        Assert.Equal(0, first.Skipped);
        Assert.Equal(2, await CountDocumentsAsync(connection, accessor));

        // ---- Import #2: the SAME archive, updateExisting=false → a no-op (all matched, nothing added) ----
        var second = await ImportAsync(connection, storage, accessor, await ExportAsync(connection, storage, accessor, _tenantA, srcId), false);
        Assert.Equal(2, second.Skipped);      // Repo + DocA both matched
        Assert.Equal(0, second.Versions);     // nothing new
        Assert.Equal(2, await CountDocumentsAsync(connection, accessor));  // no duplication
        Assert.Equal(1, await VersionCountAsync(connection, accessor, "DocA"));

        // ---- Add a v2 to DocA in tenant A, re-export, re-import with updateExisting=true ----
        accessor.TenantId = _tenantA;
        using (var db = Ctx(connection, accessor))
        {
            AddVersion(db, storage, _tenantA, docAId, 2, "v2-bytes", "a/v2.txt", userId);
            await db.SaveChangesAsync();
        }

        accessor.TenantId = _tenantB;
        var third = await ImportAsync(connection, storage, accessor, await ExportAsync(connection, storage, accessor, _tenantA, srcId), true);
        Assert.Equal(2, await CountDocumentsAsync(connection, accessor));   // still no duplicate document
        Assert.Equal(2, await VersionCountAsync(connection, accessor, "DocA")); // the new version was synced in
    }

    private static void AddVersion(SimplArchiveDbContext db, DictStorage storage, Guid tenantId, Guid docId, int number, string content, string key, Guid userId)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        storage.Objects[key] = bytes;
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = docId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = number,
            Sha256Hash = Convert.ToHexString(SHA256.HashData(bytes)),
            ObjectKey = key,
            DocumentDate = new DateOnly(2025, 1, number),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private async Task<MemoryStream> ExportAsync(SqliteConnection c, DictStorage storage, CurrentTenantAccessor accessor, Guid tenantId, Guid rootId)
    {
        var saved = accessor.TenantId;
        accessor.TenantId = tenantId;
        var zip = new MemoryStream();
        using (var db = Ctx(c, accessor))
        {
            await new RepositoryExporter(db, storage).ExportAsync(rootId, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.All, null), false, zip, CancellationToken.None);
        }
        accessor.TenantId = saved;
        zip.Position = 0;
        return zip;
    }

    private async Task<RepositoryImporter.ImportResult> ImportAsync(SqliteConnection c, DictStorage storage, CurrentTenantAccessor accessor, MemoryStream zip, bool updateExisting)
    {
        using var db = Ctx(c, accessor);
        return await new RepositoryImporter(db, storage, accessor, new WellKnownMaskSeeder(db), new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance), NoOpDocumentIndexQueue.Instance, NoOpSearchablePdfQueue.Instance, new SimplArchive.Api.Documents.PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance)).ImportAsync(zip, null, updateExisting, includePermissions: false, merge: false, SimplArchive.Api.Documents.LeafMergeMode.Rename, CancellationToken.None);
    }

    private async Task<int> CountDocumentsAsync(SqliteConnection c, CurrentTenantAccessor accessor)
    {
        using var db = Ctx(c, accessor);
        return await db.Documents.CountAsync();
    }

    private async Task<int> VersionCountAsync(SqliteConnection c, CurrentTenantAccessor accessor, string docName)
    {
        using var db = Ctx(c, accessor);
        var docId = await db.Documents.Where(d => d.Name == docName).Select(d => d.Id).SingleAsync();
        return await db.DocumentVersions.CountAsync(v => v.DocumentId == docId);
    }
}
