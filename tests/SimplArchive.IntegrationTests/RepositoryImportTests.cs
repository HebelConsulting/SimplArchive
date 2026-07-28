using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// End-to-end round-trip (ADR "Repository import"): export a subtree from tenant A, then import the archive into a
// fresh tenant B as a new repository — the tree, versions + blobs, index values, comments, the custom mask, and a
// deactivated placeholder author are all recreated in B with fresh Guids.
public class RepositoryImportTests
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

    private static SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    [Fact]
    public async Task Round_trips_a_subtree_into_a_fresh_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var storage = new DictStorage();
        var payload = Encoding.UTF8.GetBytes("hello world");
        var sha = Convert.ToHexString(SHA256.HashData(payload));

        // ---- Seed tenant A: Src → F → DocA (custom "Invoice" mask + Keywords value + a comment) ----
        accessor.TenantId = tenantA;
        Guid srcId;
        using (var db = Ctx(connection, accessor))
        {
            var userId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantA, Name = "A", CreatedAt = DateTimeOffset.UtcNow });
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "B", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantA, Email = "jane@a.test", DisplayName = "Jane", CreatedAt = DateTimeOffset.UtcNow });

            var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenantA, CreatedAt = DateTimeOffset.UtcNow };
            var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantA, MaskId = mask.Id, Name = "Invoice", RetentionYears = 7, CreatedAt = DateTimeOffset.UtcNow };
            var field = new FieldDefinition { Id = Guid.NewGuid(), TenantId = tenantA, MaskVersionId = maskVersion.Id, Name = "Keywords", DataType = FieldDataType.Text, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(mask, maskVersion, field);

            var src = new Document { Id = Guid.NewGuid(), TenantId = tenantA, Name = "Src", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            var folder = new Document { Id = Guid.NewGuid(), TenantId = tenantA, ParentId = src.Id, Name = "F", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            var docA = new Document { Id = Guid.NewGuid(), TenantId = tenantA, ParentId = folder.Id, Name = "DocA", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(src, folder, docA);
            srcId = src.Id;

            storage.Objects["a/v1.txt"] = payload;
            var versionId = Guid.NewGuid();
            db.DocumentVersions.Add(new DocumentVersion { Id = versionId, TenantId = tenantA, DocumentId = docA.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = sha, ObjectKey = "a/v1.txt", DocumentDate = new DateOnly(2025, 2, 3), CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            db.DocumentComments.Add(new DocumentComment { Id = Guid.NewGuid(), TenantId = tenantA, DocumentId = docA.Id, Body = "Nice one", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            // A markup annotation (a highlight shape) pinned to the version (ADR "Annotations in export/import").
            db.DocumentAnnotations.Add(new DocumentAnnotation { Id = Guid.NewGuid(), TenantId = tenantA, DocumentId = docA.Id, DocumentVersionId = versionId, PageIndex = 0, Kind = AnnotationKind.Highlight, PositionX = 0.1, PositionY = 0.2, Width = 0.3, Height = 0.05, Text = "", Color = "#FFEB3B", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            // DocA needs its index value before the mask assignment (required-field trigger), so assign after.
            db.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantA, DocumentId = docA.Id, FieldDefinitionId = field.Id, Value = "contract" });
            await db.SaveChangesAsync();
            docA.MaskVersionId = maskVersion.Id;
            await db.SaveChangesAsync();
        }

        // ---- Export tenant A's repository ----
        var zip = new MemoryStream();
        using (var db = Ctx(connection, accessor))
        {
            await new RepositoryExporter(db, storage).ExportAsync(srcId, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.All, null), false, zip, CancellationToken.None);
        }
        zip.Position = 0;

        // ---- Import into tenant B as a new repository ----
        accessor.TenantId = tenantB;
        RepositoryImporter.ImportResult result;
        using (var db = Ctx(connection, accessor))
        {
            result = await new RepositoryImporter(db, storage, accessor, new WellKnownMaskSeeder(db), new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance)).ImportAsync(zip, null, updateExisting: false, includePermissions: false, merge: false, SimplArchive.Api.Documents.LeafMergeMode.Rename, CancellationToken.None);
        }

        Assert.Equal("Src", result.RootName);
        Assert.Equal(3, result.Documents);

        // ---- Verify tenant B ----
        using (var db = Ctx(connection, accessor))
        {
            var root = await db.Documents.SingleAsync(d => d.Id == result.RootDocumentId);
            Assert.Null(root.ParentId); // a new repository
            Assert.Equal("Src", root.Name);

            var docA = await db.Documents.SingleAsync(d => d.Name == "DocA");
            Assert.NotNull(docA.MaskVersionId);

            // The custom mask was recreated fresh in B.
            var maskName = await db.MaskVersions.Where(m => m.Id == docA.MaskVersionId).Select(m => m.Name).SingleAsync();
            Assert.Equal("Invoice", maskName);

            // The version + blob round-trip byte-for-byte.
            var version = await db.DocumentVersions.SingleAsync(v => v.DocumentId == docA.Id);
            Assert.Equal(sha, version.Sha256Hash);
            Assert.Equal(payload, storage.Objects[version.ObjectKey]);

            // Import accounted the re-uploaded blob against tenant B's storage counter (ADR "Per-tenant storage
            // quota"): each imported version got its SizeBytes, and StorageUsedBytes is their sum.
            Assert.Equal(payload.Length, version.SizeBytes);
            var totalBlobBytes = await db.DocumentVersions.Where(v => v.SizeBytes != null).SumAsync(v => v.SizeBytes!.Value);
            Assert.True(totalBlobBytes > 0);
            Assert.Equal(totalBlobBytes, (await db.Tenants.SingleAsync(t => t.Id == tenantB)).StorageUsedBytes);

            // Index value + comment recreated.
            Assert.Equal("contract", await db.FieldValues.Where(f => f.DocumentId == docA.Id).Select(f => f.Value).SingleAsync());
            Assert.Equal("Nice one", await db.DocumentComments.Where(c => c.DocumentId == docA.Id).Select(c => c.Body).SingleAsync());

            // A deactivated placeholder author was created (matched by email, absent in B), and owns the doc.
            var jane = await db.Users.SingleAsync(u => u.NormalizedEmail == "JANE@A.TEST");
            Assert.False(jane.IsActive);
            Assert.Equal(jane.Id, docA.CreatedByUserId);

            // The annotation is recreated on the imported version (ADR "Annotations in export/import"): its kind +
            // geometry survive, it's anchored to the recreated version, and its author is the placeholder.
            var annotation = await db.DocumentAnnotations.SingleAsync(a => a.DocumentId == docA.Id);
            Assert.Equal(AnnotationKind.Highlight, annotation.Kind);
            Assert.Equal(0.3, annotation.Width!.Value, 3);
            Assert.Equal(0.05, annotation.Height!.Value, 3);
            Assert.Equal("#FFEB3B", annotation.Color);
            Assert.Equal(version.Id, annotation.DocumentVersionId);
            Assert.Equal(jane.Id, annotation.CreatedByUserId);
        }
    }
}
