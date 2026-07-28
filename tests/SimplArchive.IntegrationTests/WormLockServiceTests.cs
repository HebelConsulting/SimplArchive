using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.LegalHolds;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Worm;

namespace SimplArchive.IntegrationTests;

// Verifies WormLockService.ReconcileAsync's decision logic (ADR "WORM / immutable document versions") with a
// recording fake object store — what locks it applies given a document's retention/legal-hold state (the actual
// S3 Object Lock enforcement is covered by the WormObjectLockTests E2E against real MinIO).
public class WormLockServiceTests
{
    private sealed record RetentionCall(string Key, DateTimeOffset Until, WormLockMode Mode);

    private sealed class RecordingStorage : IObjectStorageClient
    {
        public List<RetentionCall> Retentions { get; } = [];
        public Dictionary<string, bool> LegalHolds { get; } = [];
        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StorageObject>>([]);
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) { Retentions.Add(new RetentionCall(objectKey, retainUntil, mode)); return Task.CompletedTask; }
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) { LegalHolds[objectKey] = held; return Task.CompletedTask; }
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    [Fact]
    public async Task Applies_retention_from_the_mask_and_legal_hold_from_an_active_hold()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, WormLockMode = WormLockMode.Compliance };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "u@acme.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenant.Id, CreatedAt = DateTimeOffset.UtcNow };
        var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenant.Id, MaskId = mask.Id, Name = "Retained", RetentionYears = 5, CreatedAt = DateTimeOffset.UtcNow };

        var doc = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "d", MaskVersionId = maskVersion.Id, CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        var docDate = new DateOnly(2026, 1, 1);
        var version = new DocumentVersion { Id = Guid.NewGuid(), TenantId = tenant.Id, DocumentId = doc.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = new string('0', 64), ObjectKey = "tenants/x/2026/blob.txt", DocumentDate = docDate, CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };

        var hold = new LegalHold { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Hold", PlacedByUserId = user.Id, PlacedAt = DateTimeOffset.UtcNow };
        var holdItem = new LegalHoldItem { Id = Guid.NewGuid(), TenantId = tenant.Id, LegalHoldId = hold.Id, DocumentId = doc.Id, CreatedAt = DateTimeOffset.UtcNow };

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Masks.Add(mask);
            seed.MaskVersions.Add(maskVersion);
            seed.Documents.Add(doc);
            seed.DocumentVersions.Add(version);
            seed.LegalHolds.Add(hold);
            seed.LegalHoldItems.Add(holdItem);
            await seed.SaveChangesAsync();
        }

        var storage = new RecordingStorage();
        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            var service = new WormLockService(ctx, storage, NullLogger<WormLockService>.Instance);
            await service.ReconcileAsync(doc.Id);
        }

        // Legal hold ON for the blob; retention = document date (2026-01-01) + 5y, in the tenant's mode.
        Assert.True(storage.LegalHolds[version.ObjectKey]);
        var retention = Assert.Single(storage.Retentions);
        Assert.Equal(version.ObjectKey, retention.Key);
        Assert.Equal(WormLockMode.Compliance, retention.Mode);
        Assert.Equal(new DateTimeOffset(new DateOnly(2031, 1, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), retention.Until);
    }

    [Fact]
    public async Task No_mask_and_no_hold_sets_legal_hold_off_and_no_retention()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "u@acme.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        var doc = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "d", CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        var version = new DocumentVersion { Id = Guid.NewGuid(), TenantId = tenant.Id, DocumentId = doc.Id, Status = DocumentVersionStatus.Confirmed, VersionNumber = 1, Sha256Hash = new string('0', 64), ObjectKey = "tenants/x/2026/blob2.txt", DocumentDate = new DateOnly(2026, 1, 1), CreatedByUserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Documents.Add(doc);
            seed.DocumentVersions.Add(version);
            await seed.SaveChangesAsync();
        }

        var storage = new RecordingStorage();
        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            var service = new WormLockService(ctx, storage, NullLogger<WormLockService>.Instance);
            await service.ReconcileAsync(doc.Id);
        }

        Assert.False(storage.LegalHolds[version.ObjectKey]); // legal hold explicitly off
        Assert.Empty(storage.Retentions); // no retention policy → no retention lock
    }
}
