using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Verifies AuditWormVerifier (ADR "Audit WORM segment verify"): sealed segments that match the DB verify clean;
// a DB tamper that re-chains (so the DB hash-chain check still passes) is caught because the immutable segment's
// hash no longer matches the DB's. Uses a fake store that serves back what the archiver wrote.
public class AuditWormVerifierTests
{
    // A fake object store that keeps written objects in memory and serves them back for list/get.
    private sealed class SegmentStore : IObjectStorageClient
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Objects[objectKey]));
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(Objects.ContainsKey(objectKey));
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult((long)(Objects.TryGetValue(objectKey, out var b) ? b.Length : 0));
        public async Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            Objects[objectKey] = ms.ToArray();
        }
        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StorageObject>>(Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(o => new StorageObject(o.Key, o.Value.Length, default)).ToList());
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) { Objects.Remove(objectKey); return Task.CompletedTask; }
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    private static AuditRecorder CreateRecorder(SimplArchiveDbContext db, CurrentTenantAccessor tenant, CurrentUserAccessor user) =>
        new(db, user, new CurrentServiceAccountAccessor(), new CurrentPlatformAdministratorAccessor(), tenant, new CurrentImpersonationAccessor());

    [Fact]
    public async Task Sealed_segments_verify_clean_and_a_rechained_db_tamper_is_caught()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, AuditRetentionDays = 365 };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "a@acme.test", DisplayName = "Alice", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(actor);
            await seed.SaveChangesAsync();
        }
        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        // Record a chain of 5 events (Sequence 0..4).
        using (var record = CreateContext(connection, tenantAccessor))
        {
            var recorder = CreateRecorder(record, tenantAccessor, userAccessor);
            for (var i = 0; i < 5; i++)
            {
                await recorder.RecordAsync($"Test.Action{i}", "Document", Guid.NewGuid(), $"doc {i}", $"detail {i}");
            }
        }

        // Seal them to the WORM store.
        var store = new SegmentStore();
        using (var archive = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(5, await new AuditWormArchiver(archive, store, NullLogger<AuditWormArchiver>.Instance).ArchiveAsync(tenant.Id));
        }

        // The sealed segments match the DB → clean.
        using (var check = CreateContext(connection, tenantAccessor))
        {
            var result = await new AuditWormVerifier(check, store, tenantAccessor).VerifyAsync();
            Assert.True(result.Valid);
            Assert.Equal(5, result.CheckedCount);
            Assert.Equal(1, result.SegmentCount);
        }

        // Tamper the DB at Sequence 2 AND re-chain forward (2..4) so the DB hash-chain check still passes — the
        // sophisticated tamper the DB check alone cannot catch.
        using (var tamper = CreateContext(connection, tenantAccessor))
        {
            var events = await tamper.AuditEvents.OrderBy(e => e.Sequence).ToListAsync();
            events[2].Details = "tampered";
            var previous = events[1].Hash;
            for (var i = 2; i < events.Count; i++)
            {
                events[i].Hash = AuditEventHasher.ComputeHash(previous, events[i]);
                previous = events[i].Hash;
            }
            await tamper.SaveChangesAsync();
        }

        // The DB chain check now PASSES (it was consistently re-chained)…
        using (var check = CreateContext(connection, tenantAccessor))
        {
            Assert.True((await new AuditChainVerifier(check, tenantAccessor).VerifyAsync()).Valid);
        }

        // …but the immutable WORM segment (old hash at #2) catches it.
        using (var check = CreateContext(connection, tenantAccessor))
        {
            var result = await new AuditWormVerifier(check, store, tenantAccessor).VerifyAsync();
            Assert.False(result.Valid);
            Assert.Equal(2, result.BrokenAtSequence);
            Assert.Equal("db-mismatch", result.Reason);
        }
    }
}
