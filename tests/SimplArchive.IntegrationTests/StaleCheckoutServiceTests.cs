using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Checkout;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Verifies the stale-check-out sweep (ADR "Stale check-out auto-release sweep"): a check-out idle past the
// tenant's CheckoutTtlDays is auto-released (lock cleared, cloud stash deleted, holder notified, audited); a
// recent check-out is left alone; a tenant with TTL 0 (disabled) is skipped; and the sweep is idempotent.
public class StaleCheckoutServiceTests
{
    private sealed class RecordingStorage : IObjectStorageClient
    {
        public List<string> Deleted { get; } = [];
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
        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) { Deleted.Add(objectKey); return Task.CompletedTask; }
        public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, SimplArchive.Domain.Tenants.WormLockMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(new ObjectLockStatus(null, false));
    }

    private sealed class RecordingAudit : IAuditRecorder
    {
        public List<(string Action, Guid? TargetId)> Events { get; } = [];
        public Task RecordAsync(string action, string? targetType = null, Guid? targetId = null, string? targetName = null, string? details = null, Guid? tenantId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordForActorAsync(AuditActorType actorType, Guid actorId, string actorName, Guid tenantId, string action, string? targetType = null, Guid? targetId = null, string? targetName = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Events.Add((action, targetId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<(Guid Recipient, NotificationType Type, Guid? DocumentId)> Sent { get; } = [];
        public Task NotifyAsync(Guid recipientUserId, NotificationType type, string title, string body, Guid? documentId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((recipientUserId, type, documentId));
            return Task.CompletedTask;
        }

        public Task NotifyDocumentSubscribersAsync(Guid documentId, NotificationType type, string title, string body, IEnumerable<Guid>? excludeUserIds = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    [Fact]
    public async Task Releases_stale_checkouts_and_spares_recent_ones()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, CheckoutTtlDays = 7 };
        var holder = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "h@acme.test", DisplayName = "Holder", CreatedAt = DateTimeOffset.UtcNow };

        // Idle 30 days > 7-day TTL → released.
        var stale = Doc(tenant.Id, holder.Id, "stale");
        stale.CheckedOutByUserId = holder.Id;
        stale.CheckedOutAt = DateTimeOffset.UtcNow.AddDays(-30);

        // Idle 1 day < 7-day TTL → kept.
        var recent = Doc(tenant.Id, holder.Id, "recent");
        recent.CheckedOutByUserId = holder.Id;
        recent.CheckedOutAt = DateTimeOffset.UtcNow.AddDays(-1);

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(holder);
            seed.Documents.AddRange(stale, recent);
            await seed.SaveChangesAsync();
        }

        var storage = new RecordingStorage();
        var audit = new RecordingAudit();
        var notifications = new RecordingNotifications();
        int released;
        using (var ctx = CreateContext(connection, tenantAccessor))
        {
            var service = new StaleCheckoutService(ctx, tenantAccessor, storage, audit, notifications, NullLogger<StaleCheckoutService>.Instance);
            released = await service.SweepAsync();
        }

        Assert.Equal(1, released);

        using (var check = CreateContext(connection, tenantAccessor))
        {
            tenantAccessor.TenantId = tenant.Id;
            var staleAfter = await check.Documents.SingleAsync(d => d.Id == stale.Id);
            var recentAfter = await check.Documents.SingleAsync(d => d.Id == recent.Id);
            Assert.Null(staleAfter.CheckedOutByUserId);
            Assert.Null(staleAfter.CheckedOutAt);
            Assert.Equal(holder.Id, recentAfter.CheckedOutByUserId); // untouched
        }

        Assert.Contains(CheckoutStashKey.Build(tenant.Id, holder.Id, stale.Id), storage.Deleted);
        Assert.Contains(audit.Events, e => e.Action == "Document.CheckoutExpired" && e.TargetId == stale.Id);
        Assert.Contains(notifications.Sent, n => n.Recipient == holder.Id && n.Type == NotificationType.CheckoutExpired && n.DocumentId == stale.Id);
    }

    [Fact]
    public async Task Disabled_tenant_ttl_zero_sweeps_nothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, CheckoutTtlDays = 0 };
        var holder = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "h@acme.test", DisplayName = "Holder", CreatedAt = DateTimeOffset.UtcNow };
        var doc = Doc(tenant.Id, holder.Id, "ancient");
        doc.CheckedOutByUserId = holder.Id;
        doc.CheckedOutAt = DateTimeOffset.UtcNow.AddYears(-2);

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(holder);
            seed.Documents.Add(doc);
            await seed.SaveChangesAsync();
        }

        using var ctx = CreateContext(connection, tenantAccessor);
        var service = new StaleCheckoutService(ctx, tenantAccessor, new RecordingStorage(), new RecordingAudit(), new RecordingNotifications(), NullLogger<StaleCheckoutService>.Instance);
        var released = await service.SweepAsync();

        Assert.Equal(0, released);
        tenantAccessor.TenantId = tenant.Id;
        var after = await ctx.Documents.SingleAsync(d => d.Id == doc.Id);
        Assert.Equal(holder.Id, after.CheckedOutByUserId); // TTL 0 = never expire
    }

    [Fact]
    public async Task Second_sweep_is_a_no_op()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, CheckoutTtlDays = 7 };
        var holder = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "h@acme.test", DisplayName = "Holder", CreatedAt = DateTimeOffset.UtcNow };
        var stale = Doc(tenant.Id, holder.Id, "stale");
        stale.CheckedOutByUserId = holder.Id;
        stale.CheckedOutAt = DateTimeOffset.UtcNow.AddDays(-30);

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(holder);
            seed.Documents.Add(stale);
            await seed.SaveChangesAsync();
        }

        using var ctx = CreateContext(connection, tenantAccessor);
        var service = new StaleCheckoutService(ctx, tenantAccessor, new RecordingStorage(), new RecordingAudit(), new RecordingNotifications(), NullLogger<StaleCheckoutService>.Instance);
        Assert.Equal(1, await service.SweepAsync());
        Assert.Equal(0, await service.SweepAsync()); // already released → nothing left to sweep
    }

    [Fact]
    public async Task Warns_the_holder_in_the_grace_window_once_without_releasing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        // TTL 7, warn 2 days before → a check-out idle 6 days is in the grace window but not yet due for release.
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, CheckoutTtlDays = 7, CheckoutWarningDays = 2 };
        var holder = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "h@acme.test", DisplayName = "Holder", CreatedAt = DateTimeOffset.UtcNow };
        var expiring = Doc(tenant.Id, holder.Id, "expiring");
        expiring.CheckedOutByUserId = holder.Id;
        expiring.CheckedOutAt = DateTimeOffset.UtcNow.AddDays(-6);

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(holder);
            seed.Documents.Add(expiring);
            await seed.SaveChangesAsync();
        }

        var notifications = new RecordingNotifications();
        using var ctx = CreateContext(connection, tenantAccessor);
        var service = new StaleCheckoutService(ctx, tenantAccessor, new RecordingStorage(), new RecordingAudit(), notifications, NullLogger<StaleCheckoutService>.Instance);

        // First sweep: warned once, still checked out (nothing released), reminder stamped.
        Assert.Equal(0, await service.SweepAsync());
        tenantAccessor.TenantId = tenant.Id;
        var afterWarn = await ctx.Documents.SingleAsync(d => d.Id == expiring.Id);
        Assert.Equal(holder.Id, afterWarn.CheckedOutByUserId);
        Assert.NotNull(afterWarn.CheckoutReminderSentAt);
        Assert.Single(notifications.Sent, n => n.Type == NotificationType.CheckoutExpiring && n.DocumentId == expiring.Id);

        // Second sweep: already warned → no duplicate warning.
        Assert.Equal(0, await service.SweepAsync());
        Assert.Single(notifications.Sent, n => n.Type == NotificationType.CheckoutExpiring && n.DocumentId == expiring.Id);
    }

    private static Document Doc(Guid tenantId, Guid userId, string name) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = name,
        CreatedByUserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
