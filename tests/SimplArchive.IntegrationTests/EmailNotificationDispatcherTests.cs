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

// Verifies the email-notification dispatcher (ADR "Email notifications (SMTP)"): it emails the not-yet-emailed
// notifications, stamps EmailedAt on success (so they aren't re-sent), spans tenants, and leaves EmailedAt null
// on a send failure so the next sweep retries — without stalling the rest of the batch.
public class EmailNotificationDispatcherTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());

    private sealed record SentEmail(string Address, string Subject);

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<SentEmail> Sent { get; } = [];
        // Addresses in this set throw on send (simulating a failing recipient).
        public HashSet<string> FailFor { get; } = [];

        public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default)
        {
            if (FailFor.Contains(toAddress))
            {
                throw new InvalidOperationException($"simulated send failure for {toAddress}");
            }

            Sent.Add(new SentEmail(toAddress, subject));
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

    [Fact]
    public async Task Emails_pending_notifications_stamps_them_and_does_not_resend()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "rcpt@acme.test", DisplayName = "Recipient", CreatedAt = DateTimeOffset.UtcNow };
        var pending = Pending(tenant.Id, user.Id, "Review requested");
        var already = Pending(tenant.Id, user.Id, "Old");
        already.EmailedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        using (var seed = CreateContext(connection)) { seed.Tenants.Add(tenant); seed.Users.Add(user); seed.Notifications.AddRange(pending, already); await seed.SaveChangesAsync(); }

        var sender = new RecordingEmailSender();
        int sent;
        using (var act = CreateContext(connection))
        {
            var dispatcher = new EmailNotificationDispatcher(act, sender, NullLogger<EmailNotificationDispatcher>.Instance);
            sent = await dispatcher.DispatchPendingAsync();
        }

        // Only the un-emailed one is sent, to the recipient's address, with the notification title as subject.
        Assert.Equal(1, sent);
        var email = Assert.Single(sender.Sent);
        Assert.Equal("rcpt@acme.test", email.Address);
        Assert.Equal("Review requested", email.Subject);

        using (var read = CreateContext(connection))
        {
            Assert.NotNull((await read.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == pending.Id)).EmailedAt);
        }

        // A second pass sends nothing (the first is now stamped, the other was already emailed).
        using (var again = CreateContext(connection))
        {
            var dispatcher = new EmailNotificationDispatcher(again, sender, NullLogger<EmailNotificationDispatcher>.Instance);
            Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        }

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_muted_type_is_suppressed_but_marked_handled_while_other_types_still_email()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var user = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "rcpt@acme.test", DisplayName = "Recipient", CreatedAt = DateTimeOffset.UtcNow };
        var muted = new Notification { Id = Guid.NewGuid(), TenantId = tenant.Id, RecipientUserId = user.Id, Type = NotificationType.CommentPosted, Title = "New comment", Body = "b", CreatedAt = DateTimeOffset.UtcNow };
        var kept = new Notification { Id = Guid.NewGuid(), TenantId = tenant.Id, RecipientUserId = user.Id, Type = NotificationType.ReviewAssigned, Title = "Review requested", Body = "b", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(user);
            seed.Notifications.AddRange(muted, kept);
            // The user muted the CommentPosted email channel; ReviewAssigned is left at its default (on).
            seed.UserNotificationPreferences.Add(new UserNotificationPreference { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = user.Id, Type = NotificationType.CommentPosted, EmailEnabled = false });
            await seed.SaveChangesAsync();
        }

        var sender = new RecordingEmailSender();
        using (var act = CreateContext(connection))
        {
            var dispatcher = new EmailNotificationDispatcher(act, sender, NullLogger<EmailNotificationDispatcher>.Instance);
            Assert.Equal(1, await dispatcher.DispatchPendingAsync()); // only the non-muted one counts as sent
        }

        // The muted type was not emailed; the other was.
        var email = Assert.Single(sender.Sent);
        Assert.Equal("Review requested", email.Subject);

        using (var read = CreateContext(connection))
        {
            // Both are marked handled (EmailedAt set) so neither is re-scanned — the muted one is suppressed, not retried.
            Assert.NotNull((await read.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == muted.Id)).EmailedAt);
            Assert.NotNull((await read.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == kept.Id)).EmailedAt);
        }
    }

    [Fact]
    public async Task Spans_tenants()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var t1 = new Tenant { Id = Guid.NewGuid(), Name = "T1", CreatedAt = DateTimeOffset.UtcNow };
        var t2 = new Tenant { Id = Guid.NewGuid(), Name = "T2", CreatedAt = DateTimeOffset.UtcNow };
        var u1 = new User { Id = Guid.NewGuid(), TenantId = t1.Id, Email = "a@t1.test", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow };
        var u2 = new User { Id = Guid.NewGuid(), TenantId = t2.Id, Email = "b@t2.test", DisplayName = "B", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection)) { seed.Tenants.AddRange(t1, t2); seed.Users.AddRange(u1, u2); seed.Notifications.AddRange(Pending(t1.Id, u1.Id, "One"), Pending(t2.Id, u2.Id, "Two")); await seed.SaveChangesAsync(); }

        var sender = new RecordingEmailSender();
        using (var act = CreateContext(connection))
        {
            var dispatcher = new EmailNotificationDispatcher(act, sender, NullLogger<EmailNotificationDispatcher>.Instance);
            Assert.Equal(2, await dispatcher.DispatchPendingAsync());
        }

        Assert.Contains(sender.Sent, e => e.Address == "a@t1.test");
        Assert.Contains(sender.Sent, e => e.Address == "b@t2.test");
    }

    [Fact]
    public async Task A_failed_send_stays_unemailed_and_does_not_stall_the_batch()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var bad = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "bad@acme.test", DisplayName = "Bad", CreatedAt = DateTimeOffset.UtcNow };
        var good = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "good@acme.test", DisplayName = "Good", CreatedAt = DateTimeOffset.UtcNow };
        var badNote = Pending(tenant.Id, bad.Id, "For bad");
        var goodNote = Pending(tenant.Id, good.Id, "For good");
        using (var seed = CreateContext(connection)) { seed.Tenants.Add(tenant); seed.Users.AddRange(bad, good); seed.Notifications.AddRange(badNote, goodNote); await seed.SaveChangesAsync(); }

        var sender = new RecordingEmailSender();
        sender.FailFor.Add("bad@acme.test");
        using (var act = CreateContext(connection))
        {
            var dispatcher = new EmailNotificationDispatcher(act, sender, NullLogger<EmailNotificationDispatcher>.Instance);
            Assert.Equal(1, await dispatcher.DispatchPendingAsync()); // only the good one counts as sent
        }

        Assert.Contains(sender.Sent, e => e.Address == "good@acme.test");
        using (var read = CreateContext(connection))
        {
            Assert.NotNull((await read.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == goodNote.Id)).EmailedAt); // stamped
            Assert.Null((await read.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == badNote.Id)).EmailedAt);       // retryable
        }
    }
}
