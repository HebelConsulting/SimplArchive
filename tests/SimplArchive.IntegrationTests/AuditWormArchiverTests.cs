using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Audit;
using SimplArchive.Infrastructure.Persistence;
using System.Text;

namespace SimplArchive.IntegrationTests;

// Verifies AuditWormArchiver (ADR "Audit-log WORM"): it seals the contiguous run of audit events past the
// checkpoint into a WORM segment (retention-locked), advances the checkpoint, is idempotent, and stops at a
// Sequence gap. Uses a recording fake object store; real SeaweedFS Object Lock is covered by the E2E.
public class AuditWormArchiverTests
{
    private sealed class RecordingStorage : IObjectStorageClient
    {
        public Dictionary<string, byte[]> Puts { get; } = [];
        public Dictionary<string, (DateTimeOffset Until, WormLockMode Mode)> Retentions { get; } = [];
        public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Uri> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://x"));
        public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public async Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            Puts[objectKey] = ms.ToArray();
        }
        public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StorageObject>>([]);
        public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) { Retentions[objectKey] = (retainUntil, mode); return Task.CompletedTask; }
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    private static AuditEvent Event(Guid tenantId, long sequence) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Sequence = sequence,
        Hash = new string('a', 64),
        Timestamp = DateTimeOffset.UtcNow,
        ActorType = AuditActorType.User,
        ActorId = Guid.NewGuid(),
        ActorName = "Actor",
        Action = "Test.Action",
    };

    [Fact]
    public async Task Seals_a_contiguous_run_locks_it_advances_the_checkpoint_and_is_idempotent()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, AuditRetentionDays = 365, WormLockMode = WormLockMode.Compliance };
        using (var seed = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            seed.Tenants.Add(tenant);
            seed.AuditEvents.AddRange(Event(tenant.Id, 0), Event(tenant.Id, 1), Event(tenant.Id, 2));
            await seed.SaveChangesAsync();
        }

        var storage = new RecordingStorage();
        int sealed1;
        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            sealed1 = await new AuditWormArchiver(ctx, storage, NullLogger<AuditWormArchiver>.Instance).ArchiveAsync(tenant.Id);
        }

        Assert.Equal(3, sealed1);
        var key = $"tenants/{tenant.Id}/audit-worm/{0:D20}-{2:D20}.ndjson";
        Assert.True(storage.Puts.ContainsKey(key));
        Assert.Equal(3, Encoding.UTF8.GetString(storage.Puts[key]).TrimEnd('\n').Split('\n').Length); // one NDJSON line per event
        Assert.Equal(WormLockMode.Compliance, storage.Retentions[key].Mode);
        Assert.True(storage.Retentions[key].Until > DateTimeOffset.UtcNow.AddDays(300)); // ~365-day lock

        // Checkpoint advanced; a second archive is a no-op.
        using (var check = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            Assert.Equal(2, (await check.Tenants.SingleAsync(t => t.Id == tenant.Id)).AuditWormArchivedThrough);
            Assert.Equal(0, await new AuditWormArchiver(check, storage, NullLogger<AuditWormArchiver>.Instance).ArchiveAsync(tenant.Id));
        }
    }

    [Fact]
    public async Task Stops_at_a_sequence_gap_and_seals_the_tail_once_it_fills()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, AuditRetentionDays = 30 };
        using (var seed = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            seed.Tenants.Add(tenant);
            // 0,1 present, 2 missing (gap), 3 present.
            seed.AuditEvents.AddRange(Event(tenant.Id, 0), Event(tenant.Id, 1), Event(tenant.Id, 3));
            await seed.SaveChangesAsync();
        }

        var storage = new RecordingStorage();
        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            // Seals only 0..1 (stops at the gap before 2); event 3 waits.
            Assert.Equal(2, await new AuditWormArchiver(ctx, storage, NullLogger<AuditWormArchiver>.Instance).ArchiveAsync(tenant.Id));
        }
        Assert.True(storage.Puts.ContainsKey($"tenants/{tenant.Id}/audit-worm/{0:D20}-{1:D20}.ndjson"));

        // The gap fills; the next archive seals 2..3.
        using (var fill = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            fill.AuditEvents.Add(Event(tenant.Id, 2));
            await fill.SaveChangesAsync();
        }
        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            Assert.Equal(2, await new AuditWormArchiver(ctx, storage, NullLogger<AuditWormArchiver>.Instance).ArchiveAsync(tenant.Id));
        }
        Assert.True(storage.Puts.ContainsKey($"tenants/{tenant.Id}/audit-worm/{2:D20}-{3:D20}.ndjson"));
    }
}
