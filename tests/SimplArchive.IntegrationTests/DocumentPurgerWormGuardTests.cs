using Amazon.S3;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The purge path asks storage whether each blob is still immutable before destroying it (ADR "WORM / immutable
// document versions"). What it does when that question CANNOT BE ANSWERED is the whole point of these tests, and
// it was previously untested — a blanket `catch (Exception) { continue; }` treated every failure, including a
// store it could not reach, as "not locked", which is the one direction that destroys data a hold was holding.
//
// So the rule under test is asymmetric on purpose: an answer meaning "there is nothing to protect" lets the
// purge run; silence refuses it (ADR 0702).
public class DocumentPurgerWormGuardTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();

    [Fact]
    public async Task A_store_that_cannot_be_reached_refuses_the_purge()
    {
        // The dangerous case. We asked "is this locked?" and got no answer at all — so we do not know, and a
        // purge that proceeds on "do not know" is indistinguishable from one that proceeds on "not locked".
        var (purger, documents, _) = await BuildAsync(new ThrowingStorage(new HttpRequestException("no route to host")));

        await Assert.ThrowsAsync<HttpRequestException>(() => purger.PurgeAsync(documents, CancellationToken.None));
    }

    [Fact]
    public async Task A_bucket_without_object_lock_still_purges()
    {
        // The store ANSWERED, and its answer was that Object Lock is not configured here. WORM is not active, so
        // there is nothing to protect and refusing would make the feature's absence look like a fault.
        var (purger, documents, db) = await BuildAsync(new ThrowingStorage(new AmazonS3Exception("no lock configuration")));

        var purged = await purger.PurgeAsync(documents, CancellationToken.None);

        Assert.Single(purged);
        Assert.Empty(await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _documentId).ToListAsync());
    }

    [Fact]
    public async Task A_blob_that_no_longer_exists_still_purges()
    {
        // Nothing to protect: the object is already gone. This is the case the old code got right by accident,
        // for the wrong reason — it could not tell it apart from the unreachable store above.
        var (purger, documents, db) = await BuildAsync(new ThrowingStorage(new StorageObjectNotFoundException("tenants/x/2026/v.pdf")));

        var purged = await purger.PurgeAsync(documents, CancellationToken.None);

        Assert.Single(purged);
        Assert.Empty(await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _documentId).ToListAsync());
    }

    [Fact]
    public async Task A_locked_blob_refuses_the_purge()
    {
        // The counterpart that keeps the guard honest: if everything above merely proved "the purge runs", the
        // WORM check could be absent entirely and the tests would still pass.
        var (purger, documents, _) = await BuildAsync(new LockedStorage(DateTimeOffset.UtcNow.AddYears(5)));

        await Assert.ThrowsAsync<WormLockedException>(() => purger.PurgeAsync(documents, CancellationToken.None));
    }

    private async Task<(DocumentPurger Purger, List<Document> Documents, SimplArchiveDbContext Db)> BuildAsync(
        IObjectStorageClient storage)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var tenant = new CurrentTenantAccessor { TenantId = _tenantId };
        var db = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);
        await db.Database.EnsureCreatedAsync();

        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new User { Id = _userId, TenantId = _tenantId, Email = "u@acme.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
        db.Documents.Add(new Document
        {
            Id = _documentId,
            TenantId = _tenantId,
            Name = "Doc",
            CreatedByUserId = _userId,
            CreatedAt = DateTimeOffset.UtcNow,
            DeletedAt = DateTimeOffset.UtcNow, // in the recycle bin: purge only ever runs on soft-deleted rows
        });
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DocumentId = _documentId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = new string('a', 64),
            ObjectKey = "tenants/x/2026/v.pdf",
            CreatedByUserId = _userId,
            CreatedAt = DateTimeOffset.UtcNow,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        await db.SaveChangesAsync();

        var documents = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).ToListAsync();
        var purger = new DocumentPurger(db, storage, new NoOpDocumentIndexQueue(), new NeverHeld(), new NoOpQuota());
        return (purger, documents, db);
    }

    private sealed class ThrowingStorage(Exception thrown) : NotUsedObjectStorage
    {
        public override Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw thrown;
    }

    private sealed class LockedStorage(DateTimeOffset until) : NotUsedObjectStorage
    {
        public override Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new ObjectLockStatus(until, false));
    }

    private sealed class NeverHeld : ILegalHoldService
    {
        public Task<bool> IsFrozenAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> AnyDirectlyHeldAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NoOpQuota : IStorageQuotaService
    {
        public Task<bool> CanStoreAsync(Guid tenantId, long additionalBytes, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task AdjustUsageAsync(Guid tenantId, long deltaBytes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Only the two members the purge path uses are real; everything else throws, so a test that starts relying
    // on a different call fails loudly instead of silently exercising a no-op.
    private abstract class NotUsedObjectStorage : IObjectStorageClient
    {
        public virtual Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StorageObject>>([]);

        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
