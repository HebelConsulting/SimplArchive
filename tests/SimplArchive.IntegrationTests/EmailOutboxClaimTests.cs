using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The property the EmailOutbox exists for (#1425, ADRs 0836/0840): two sweeps over one notification send ONE
// email.
//
// This is written as the inverse of a measured failure rather than from the design. Claiming with the retry
// COUNTER was tried first and proved impossible: the counter is not in the pending predicate, so both sweeps
// still matched and the assertion failed with `Collection contained 2 items ["rcpt@acme.test",
// "rcpt@acme.test"]`. The queue's `State` IS in the predicate, which is the whole difference between a claim and
// bookkeeping — so this file asserts the thing that used to be false.
public class EmailOutboxClaimTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());

    private static SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient InertEnvelopeClient() =>
        new(new InertHttpClientFactory(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient>.Instance);

    private sealed class InertHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static EmailNotificationDispatcher Dispatcher(SimplArchiveDbContext context, IEmailSender sender) =>
        new(context, sender, InertEnvelopeClient(), NullLogger<EmailNotificationDispatcher>.Instance, NoOpAuditRecorder.Instance, new NoModuleScopeFactory(context));

    /// <summary>
    /// A sender that runs a SECOND sweep from inside the first one's send — the interleaving that matters.
    /// </summary>
    /// <remarks>
    /// Two real sweeps cannot be raced deterministically in a test, and a test that merely runs them one after
    /// the other proves nothing: the first finishes and deletes the row, so the second finds an empty queue
    /// whether or not a claim exists. Re-entering DURING the send puts the second sweep exactly where the defect
    /// lived — the first has taken the work and has not yet recorded an outcome — which is the only moment at
    /// which a missing claim produces two emails.
    /// </remarks>
    private sealed class ReentrantEmailSender : IEmailSender
    {
        private readonly SqliteConnection _connection;
        private bool _entered;

        public ReentrantEmailSender(SqliteConnection connection) => _connection = connection;

        public List<string> Sent { get; } = [];

        /// <summary>What the sweep that ran INSIDE the send reported sending. Must be zero.</summary>
        public int SentByTheSecondSweep { get; private set; } = -1;

        public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default) =>
            SendAsync(toAddress, toName, subject, body, envelopeCertificatePem: null, cancellationToken);

        public async Task SendAsync(string toAddress, string toName, string subject, string body,
            string? envelopeCertificatePem, CancellationToken cancellationToken = default)
        {
            Sent.Add(toAddress);

            if (!_entered)
            {
                _entered = true;

                // A plain sender for the inner sweep: if it sends anything, that is the duplicate.
                var inner = new CountingEmailSender();
                using var context = CreateContext(_connection);
                SentByTheSecondSweep = await Dispatcher(context, inner).DispatchPendingAsync(cancellationToken);
                Sent.AddRange(inner.Sent);
            }
        }
    }

    private sealed class CountingEmailSender : IEmailSender
    {
        public List<string> Sent { get; } = [];

        public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default) =>
            SendAsync(toAddress, toName, subject, body, envelopeCertificatePem: null, cancellationToken);

        public Task SendAsync(string toAddress, string toName, string subject, string body,
            string? envelopeCertificatePem, CancellationToken cancellationToken = default)
        {
            Sent.Add(toAddress);
            return Task.CompletedTask;
        }
    }

    private static async Task<(Tenant Tenant, User User, Notification Notification)> SeedAsync(SqliteConnection connection)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = "rcpt@acme.test",
            DisplayName = "Recipient",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            RecipientUserId = user.Id,
            Type = NotificationType.ReviewAssigned,
            Title = "Review requested",
            Body = "b",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var seed = CreateContext(connection);
        seed.Tenants.Add(tenant);
        seed.Users.Add(user);
        seed.Notifications.Add(notification);
        await seed.SaveChangesAsync();
        return (tenant, user, notification);
    }

    [Fact]
    public async Task Inserting_a_notification_enqueues_exactly_one_row_with_no_caller_doing_anything()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var (_, _, notification) = await SeedAsync(connection);

        // The seed added a Notification and NOTHING else — no queue call. This is the property that makes the
        // SaveChanges hook worth its unusual placement: a creation site cannot forget what it never does.
        using var read = CreateContext(connection);
        var row = Assert.Single(await read.EmailOutbox.ToListAsync());
        Assert.Equal(notification.Id, row.NotificationId);
        Assert.Equal(notification.TenantId, row.TenantId);
        Assert.Equal(EmailOutboxState.Pending, row.State);
        Assert.Null(row.ClaimedAt);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task Two_sweeps_over_one_notification_send_ONE_email()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        await SeedAsync(connection);

        var sender = new ReentrantEmailSender(connection);
        int sent;
        using (var act = CreateContext(connection))
        {
            sent = await Dispatcher(act, sender).DispatchPendingAsync();
        }

        // THE ASSERTION THAT USED TO FAIL. With the claim in the pending predicate, the second sweep finds the
        // row in `Sending` and sends nothing.
        //
        // CALIBRATED, and the first attempt at calibrating it was WRONG in a way worth recording. Making the
        // dispatcher ignore the claim's result (`_ = won;` instead of `continue`) left this test PASSING — because
        // the exclusion is the STATE WRITE, not the reaction to it: the row had already moved to `Sending`, so the
        // inner sweep's read matched nothing either way. A guard verified that way would have been measuring
        // nothing. Removing the `SetProperty(o => o.State, Sending)` instead — leaving the row Pending across the
        // send, which is exactly the pre-outbox shape — fails here with "The collection contained 2 items", the
        // same words the EmailAttempts attempt failed with.
        Assert.Equal("rcpt@acme.test", Assert.Single(sender.Sent));
        Assert.Equal(1, sent);
        Assert.Equal(0, sender.SentByTheSecondSweep);

        // …and the row is gone, so neither sweep left work behind.
        using var read = CreateContext(connection);
        Assert.Empty(await read.EmailOutbox.ToListAsync());
    }

    [Fact]
    public async Task A_claim_abandoned_by_a_dead_sweep_is_reclaimed_and_retried()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        await SeedAsync(connection);

        // A sweep that claimed the row and died: `Sending`, claimed long ago. Written directly, because the only
        // other way to produce it is to kill a process.
        using (var strand = CreateContext(connection))
        {
            var row = await strand.EmailOutbox.SingleAsync();
            row.State = EmailOutboxState.Sending;
            row.ClaimedAt = DateTimeOffset.UtcNow.AddHours(-1);
            await strand.SaveChangesAsync();
        }

        var sender = new CountingEmailSender();
        using (var act = CreateContext(connection))
        {
            Assert.Equal(1, await Dispatcher(act, sender).DispatchPendingAsync());
        }

        // Without the reclaim this row is stranded forever and the email is NEVER sent — silence being the one
        // outcome this path exists to avoid.
        Assert.Equal("rcpt@acme.test", Assert.Single(sender.Sent));

        using var read = CreateContext(connection);
        Assert.Empty(await read.EmailOutbox.ToListAsync());
    }

    [Fact]
    public async Task A_fresh_claim_is_NOT_reclaimed_while_a_sweep_is_still_working_on_it()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        await SeedAsync(connection);

        // Claimed a moment ago — a healthy sweep mid-send. The calibration for the test above: if the cutoff were
        // ignored, the reclaim would steal work from a LIVE sweep and deliver every email twice, which is worse
        // than the stranding it fixes.
        using (var claim = CreateContext(connection))
        {
            var row = await claim.EmailOutbox.SingleAsync();
            row.State = EmailOutboxState.Sending;
            row.ClaimedAt = DateTimeOffset.UtcNow;
            await claim.SaveChangesAsync();
        }

        var sender = new CountingEmailSender();
        using (var act = CreateContext(connection))
        {
            Assert.Equal(0, await Dispatcher(act, sender).DispatchPendingAsync());
        }

        Assert.Empty(sender.Sent);

        using var read = CreateContext(connection);
        var untouched = await read.EmailOutbox.SingleAsync();
        Assert.Equal(EmailOutboxState.Sending, untouched.State);
        Assert.Equal(0, untouched.Attempts);
    }
}
