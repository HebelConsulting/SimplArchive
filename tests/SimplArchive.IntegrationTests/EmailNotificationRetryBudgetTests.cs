using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Issue #433 / ADR 0612. A send that fails deliberately leaves EmailedAt null so the next sweep retries — which
// is right for a transient failure and catastrophic for an address that can never receive mail: the row stays
// pending forever, and a batch (200) of such rows is entirely hopeless, so every legitimate notification behind
// them is never looked at again. The symptom is mail that simply does not arrive, which is why it needs a test
// rather than an eye.
public class EmailNotificationRetryBudgetTests
{
    private const int MaxEmailAttempts = 5; // mirrors the dispatcher's budget

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());

    private sealed class FailingSender(HashSet<string> failFor, bool permanently = false) : IEmailSender
    {
        public List<string> Attempts { get; } = [];

        public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default)
        {
            Attempts.Add(toAddress);
            if (!failFor.Contains(toAddress))
            {
                return Task.CompletedTask;
            }

            throw permanently
                ? new PermanentEmailFailureException($"550 no such mailbox: {toAddress}")
                : new InvalidOperationException($"connection refused for {toAddress}");
        }
    }

    [Fact]
    public async Task Giving_up_writes_the_audit_row_through_the_REAL_recorder_with_no_ambient_principal()
    {
        // The #1312 regression test the issue itself demands: the fake above proves the dispatcher CALLS the
        // recorder; only the real one can prove the event survives a scope with no principal accessors — the
        // exact condition under which the old RecordAsync call silently dropped it in production while three
        // fakes stayed green.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, badUser, _) = await SeedAsync(connection);

        var doomed = Pending(tenantId, badUser, "Review requested");
        using (var seed = CreateContext(connection)) { seed.Notifications.Add(doomed); await seed.SaveChangesAsync(); }

        var sender = new FailingSender(["gone@nowhere.invalid"], permanently: true);
        using (var act = CreateContext(connection))
        {
            // Bare accessors throughout — exactly what EmailNotificationWorker's CreateScope() yields.
            var recorder = new SimplArchive.Infrastructure.Audit.AuditRecorder(
                act, new CurrentUserAccessor(), new CurrentServiceAccountAccessor(),
                new CurrentPlatformAdministratorAccessor(), new CurrentTenantAccessor(),
                new CurrentImpersonationAccessor(), new CurrentSystemActorAccessor(), TimeProvider.System,
                NullLogger<SimplArchive.Infrastructure.Audit.AuditRecorder>.Instance);
            await new EmailNotificationDispatcher(act, sender, InertEnvelopeClient(), NullLogger<EmailNotificationDispatcher>.Instance, recorder)
                .DispatchPendingAsync();
        }

        using var read = CreateContext(connection);
        var row = Assert.Single(await read.AuditEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("Notification.EmailAbandoned", row.Action);
        Assert.Equal(SimplArchive.Domain.Audit.AuditActorType.System, row.ActorType);
        Assert.Equal("Notification email", row.ActorName);
        Assert.Equal(tenantId, row.TenantId);
    }

    private sealed class CountingAudit : IAuditRecorder
    {
        public List<(string Action, string? Target, string? Actor)> Events { get; } = [];

        // BOTH channels record (#1312). The first version stubbed RecordForActorAsync to nothing, which is
        // precisely how the real drop stayed green: the dispatcher "called the recorder" — the wrong method,
        // whose production implementation discarded the event — and this fake could not tell the difference.
        public Task RecordAsync(string action, string? targetType = null, Guid? targetId = null, string? targetName = null,
            string? details = null, Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            Events.Add((action, targetName, null));
            return Task.CompletedTask;
        }

        public Task RecordForActorAsync(SimplArchive.Domain.Audit.AuditActorType actorType, Guid actorId, string actorName,
            Guid tenantId, string action, string? targetType = null, Guid? targetId = null, string? targetName = null,
            string? details = null, CancellationToken cancellationToken = default)
        {
            Events.Add((action, targetName, $"{actorType}:{actorName}"));
            return Task.CompletedTask;
        }
    }

    private static Notification Pending(Guid tenantId, Guid recipientId, string title) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        RecipientUserId = recipientId,
        Type = NotificationType.ReviewAssigned,
        Title = title,
        Body = $"{title} body",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<(Guid TenantId, Guid BadUser, Guid GoodUser)> SeedAsync(SqliteConnection connection)
    {
        using var setup = CreateContext(connection);
        await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var bad = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "gone@nowhere.invalid", DisplayName = "Departed", CreatedAt = DateTimeOffset.UtcNow };
        var good = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "rcpt@acme.test", DisplayName = "Recipient", CreatedAt = DateTimeOffset.UtcNow };
        setup.Tenants.Add(tenant);
        setup.Users.AddRange(bad, good);
        await setup.SaveChangesAsync();
        return (tenant.Id, bad.Id, good.Id);
    }

    [Fact]
    public async Task A_failing_address_is_abandoned_after_the_retry_budget_and_audited()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, badUser, _) = await SeedAsync(connection);

        var doomed = Pending(tenantId, badUser, "Review requested");
        using (var seed = CreateContext(connection)) { seed.Notifications.Add(doomed); await seed.SaveChangesAsync(); }

        var sender = new FailingSender(["gone@nowhere.invalid"]);
        var audit = new CountingAudit();

        // Sweep repeatedly, as the worker's timer would.
        for (var i = 0; i < MaxEmailAttempts + 3; i++)
        {
            using var act = CreateContext(connection);
            await new EmailNotificationDispatcher(act, sender, InertEnvelopeClient(), NullLogger<EmailNotificationDispatcher>.Instance, audit)
                .DispatchPendingAsync();
        }

        // It is tried exactly the budget — not once more, however many sweeps run.
        Assert.Equal(MaxEmailAttempts, sender.Attempts.Count);

        using var read = CreateContext(connection);
        var row = await read.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == doomed.Id);
        Assert.Equal(MaxEmailAttempts, row.EmailAttempts);
        Assert.NotNull(row.EmailFailedAt);
        Assert.Null(row.EmailedAt); // the mail never went; the in-app notification is untouched

        // And an administrator can see it happened without reading a log — recorded with an EXPLICIT
        // System actor (#1312): this sweep has no ambient principal, so the ambient-resolving RecordAsync
        // would have warned and dropped exactly this event.
        var abandoned = Assert.Single(audit.Events);
        Assert.Equal("Notification.EmailAbandoned", abandoned.Action);
        Assert.Equal("Review requested", abandoned.Target);
        Assert.Equal("System:Notification email", abandoned.Actor);
    }

    [Fact]
    public async Task A_permanent_rejection_gives_up_at_once_rather_than_spending_the_budget()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, badUser, _) = await SeedAsync(connection);

        var doomed = Pending(tenantId, badUser, "Review requested");
        using (var seed = CreateContext(connection)) { seed.Notifications.Add(doomed); await seed.SaveChangesAsync(); }

        var sender = new FailingSender(["gone@nowhere.invalid"], permanently: true);

        for (var i = 0; i < 3; i++)
        {
            using var act = CreateContext(connection);
            await new EmailNotificationDispatcher(act, sender, InertEnvelopeClient(), NullLogger<EmailNotificationDispatcher>.Instance, new CountingAudit())
                .DispatchPendingAsync();
        }

        // "No such mailbox" cannot succeed on retry, so it is not retried.
        Assert.Single(sender.Attempts);

        using var read = CreateContext(connection);
        var row = await read.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == doomed.Id);
        Assert.Equal(1, row.EmailAttempts);
        Assert.NotNull(row.EmailFailedAt);
    }

    // The defect itself: a hopeless row must not keep a good one from ever being looked at.
    [Fact]
    public async Task A_hopeless_notification_does_not_stall_the_ones_behind_it()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, badUser, goodUser) = await SeedAsync(connection);

        // Enough doomed rows to fill the batch, seeded FIRST so they sort ahead of the good one by Id... which is
        // not guaranteed by a random Guid, so the good one is asserted on its own terms below rather than by
        // position: what matters is that it is eventually sent, not that it is sent first.
        using (var seed = CreateContext(connection))
        {
            for (var i = 0; i < 20; i++)
            {
                seed.Notifications.Add(Pending(tenantId, badUser, $"Doomed {i}"));
            }

            seed.Notifications.Add(Pending(tenantId, goodUser, "Legitimate"));
            await seed.SaveChangesAsync();
        }

        var sender = new FailingSender(["gone@nowhere.invalid"]);
        for (var i = 0; i < MaxEmailAttempts + 2; i++)
        {
            using var act = CreateContext(connection);
            await new EmailNotificationDispatcher(act, sender, InertEnvelopeClient(), NullLogger<EmailNotificationDispatcher>.Instance, new CountingAudit())
                .DispatchPendingAsync();
        }

        // The good one was delivered, and the doomed ones stopped consuming sweeps.
        Assert.Contains("rcpt@acme.test", sender.Attempts);
        Assert.Equal(20 * MaxEmailAttempts + 1, sender.Attempts.Count);

        using var read = CreateContext(connection);
        var pendingLeft = await read.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.EmailedAt == null && n.EmailFailedAt == null);
        Assert.Equal(0, pendingLeft); // nothing is left circling forever
    }

    // An INERT envelope client for these tests: no Encryption:ServiceUrl configured, so EnabledFor is false
    // and the certificate lookup answers null without touching the network — plaintext dispatch, the
    // pre-#1334 behaviour these tests pin.
    private static SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient InertEnvelopeClient() =>
        new(new InertHttpClientFactory(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient>.Instance);

    private sealed class InertHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
