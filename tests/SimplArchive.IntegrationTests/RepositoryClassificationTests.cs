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

// Classification in export/import (ADR "Classification in export/import"): a document's sensitivity label + the
// mask's default label always round-trip into a fresh tenant (merged by name); with the permissions toggle on, a
// principal's clearance travels too, applied max-never-lower so a re-import never downgrades a destination user.
public class RepositoryClassificationTests
{
    private sealed class DictStorage : IObjectStorageClient
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Objects.GetValueOrDefault(objectKey, [])));
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
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StorageObject>>([]);
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private readonly Guid _tenantA = Guid.NewGuid();
    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private static RepositoryImporter Importer(SimplArchiveDbContext db, DictStorage storage, CurrentTenantAccessor accessor) =>
        new(db, storage, accessor, new WellKnownMaskSeeder(db),
            new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance), NoOpDocumentIndexQueue.Instance, NoOpSearchablePdfQueue.Instance, new SimplArchive.Api.Documents.PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance));

    // Seeds tenant A: a "Confidential" label (rank 3, watermarked), a custom "Contract" mask defaulting to it, and a
    // root document labelled Confidential + created by jane (clearance 5). Returns the export bytes (permissions on).
    private async Task<MemoryStream> SeedAndExportAsync(SqliteConnection c, CurrentTenantAccessor accessor, DictStorage storage, Guid tenantB)
    {
        accessor.TenantId = _tenantA;
        Guid repoId;
        using (var db = Ctx(c, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantA, Name = "A", CreatedAt = DateTimeOffset.UtcNow });
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "B", CreatedAt = DateTimeOffset.UtcNow });

            var jane = new User { Id = Guid.NewGuid(), TenantId = _tenantA, Email = "jane@a.test", DisplayName = "Jane", ClearanceRank = 5, CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(jane);

            var label = new SensitivityLabelDefinition { Id = Guid.NewGuid(), TenantId = _tenantA, Name = "Confidential", Rank = 3, Color = "#ef6c00", Watermark = true, CreatedAt = DateTimeOffset.UtcNow };
            db.SensitivityLabelDefinitions.Add(label);

            var mask = new Mask { Id = Guid.NewGuid(), TenantId = _tenantA, CreatedAt = DateTimeOffset.UtcNow };
            var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = _tenantA, MaskId = mask.Id, Name = "Contract", DefaultSensitivityLabelId = label.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Masks.Add(mask);
            db.MaskVersions.Add(maskVersion);
            await db.SaveChangesAsync();

            var repo = new Document { Id = Guid.NewGuid(), TenantId = _tenantA, Name = "Repo", BreaksInheritance = true, MaskVersionId = maskVersion.Id, SensitivityLabelId = label.Id, CreatedByUserId = jane.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Documents.Add(repo);
            repoId = repo.Id;
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
    public async Task Label_mask_default_and_clearance_round_trip()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantB = Guid.NewGuid();
        var storage = new DictStorage();
        var zip = await SeedAndExportAsync(connection, accessor, storage, tenantB);

        accessor.TenantId = tenantB;
        RepositoryImporter.ImportResult result;
        using (var db = Ctx(connection, accessor))
        {
            result = await Importer(db, storage, accessor).ImportAsync(zip, null, updateExisting: false, includePermissions: true, merge: false, LeafMergeMode.Rename, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            // The label was recreated in tenant B with its rank/colour/watermark.
            var label = await db.SensitivityLabelDefinitions.SingleAsync(l => l.TenantId == tenantB && l.Name == "Confidential");
            Assert.Equal(3, label.Rank);
            Assert.True(label.Watermark);
            Assert.Equal("#ef6c00", label.Color);

            // The root document carries it, and the imported "Contract" mask defaults to it.
            var root = await db.Documents.SingleAsync(d => d.Id == result.RootDocumentId);
            Assert.Equal(label.Id, root.SensitivityLabelId);
            var maskVersion = await db.MaskVersions.SingleAsync(m => m.TenantId == tenantB && m.Name == "Contract");
            Assert.Equal(label.Id, maskVersion.DefaultSensitivityLabelId);

            // jane's clearance travelled (placeholder created with clearance 5).
            var jane = await db.Users.SingleAsync(u => u.NormalizedEmail == "JANE@A.TEST");
            Assert.Equal(5, jane.ClearanceRank);
        }
    }

    [Fact]
    public async Task Clearance_is_applied_max_never_lower()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantB = Guid.NewGuid();
        var storage = new DictStorage();
        var zip = await SeedAndExportAsync(connection, accessor, storage, tenantB);

        // Tenant B already has jane at a HIGHER clearance (7) than the archive's (5).
        accessor.TenantId = tenantB;
        using (var db = Ctx(connection, accessor))
        {
            db.Users.Add(new User { Id = Guid.NewGuid(), TenantId = tenantB, Email = "jane@a.test", DisplayName = "Jane B", ClearanceRank = 7, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            await Importer(db, storage, accessor).ImportAsync(zip, null, updateExisting: false, includePermissions: true, merge: false, LeafMergeMode.Rename, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            // The import must NOT lower jane from 7 to 5.
            var jane = await db.Users.SingleAsync(u => u.NormalizedEmail == "JANE@A.TEST");
            Assert.Equal(7, jane.ClearanceRank);
        }
    }

    [Fact]
    public async Task Clearance_is_not_carried_without_permissions()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var tenantB = Guid.NewGuid();
        var storage = new DictStorage();
        var zip = await SeedAndExportAsync(connection, accessor, storage, tenantB);

        accessor.TenantId = tenantB;
        RepositoryImporter.ImportResult result;
        using (var db = Ctx(connection, accessor))
        {
            // Permissions off — labels still travel (document metadata), but clearance does not.
            result = await Importer(db, storage, accessor).ImportAsync(zip, null, updateExisting: false, includePermissions: false, merge: false, LeafMergeMode.Rename, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            var root = await db.Documents.SingleAsync(d => d.Id == result.RootDocumentId);
            Assert.NotNull(root.SensitivityLabelId); // label still classified
            var jane = await db.Users.SingleAsync(u => u.NormalizedEmail == "JANE@A.TEST");
            Assert.Equal(0, jane.ClearanceRank); // clearance not carried
        }
    }
}
