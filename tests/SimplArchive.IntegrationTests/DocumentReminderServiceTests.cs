using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Reminders;

namespace SimplArchive.IntegrationTests;

// Verifies the document-reminder sweep (ADR "Document reminders"): a due one-shot fires (notifies the target,
// stamps FiredAt); a due recurring one fires and advances RemindAt to the next future occurrence without
// stamping; a not-yet-due reminder is untouched.
public class DocumentReminderServiceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());

    [Fact]
    public async Task Fires_due_reminders_advances_recurring_and_leaves_future_ones()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var target = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "t@acme.test", DisplayName = "Target", CreatedAt = DateTimeOffset.UtcNow };
        var doc = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Contract", CreatedByUserId = target.Id, CreatedAt = DateTimeOffset.UtcNow };

        var now = DateTimeOffset.UtcNow;
        var oneShot = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = now.AddMinutes(-5), Recurrence = ReminderRecurrence.None, CreatedByUserId = target.Id, CreatedAt = now.AddDays(-1) };
        var recurring = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = now.AddDays(-10), Recurrence = ReminderRecurrence.Weekly, CreatedByUserId = target.Id, CreatedAt = now.AddDays(-20) };
        var future = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = now.AddDays(3), Recurrence = ReminderRecurrence.None, CreatedByUserId = target.Id, CreatedAt = now };

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(target);
            seed.Documents.Add(doc);
            seed.DocumentReminders.AddRange(oneShot, recurring, future);
            await seed.SaveChangesAsync();
        }

        using (var act = CreateContext(connection))
        {
            var fired = await new DocumentReminderService(act).SweepAsync();
            Assert.Equal(2, fired); // the one-shot + the recurring, not the future one
        }

        using var read = CreateContext(connection);
        // Two notifications for the target, both DocumentReminder.
        var notifications = await read.Notifications.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n => Assert.Equal(NotificationType.DocumentReminder, n.Type));
        Assert.All(notifications, n => Assert.Equal(target.Id, n.RecipientUserId));

        var reminders = await read.DocumentReminders.IgnoreQueryFilters().ToDictionaryAsync(r => r.Id);
        // One-shot: FiredAt stamped (done).
        Assert.NotNull(reminders[oneShot.Id].FiredAt);
        // Recurring: still pending, RemindAt advanced to the next future weekly occurrence (within 7 days, on
        // the same weekday as the original).
        Assert.Null(reminders[recurring.Id].FiredAt);
        Assert.True(reminders[recurring.Id].RemindAt > now);
        Assert.True(reminders[recurring.Id].RemindAt <= now.AddDays(7));
        Assert.Equal(now.AddDays(-10).DayOfWeek, reminders[recurring.Id].RemindAt.DayOfWeek);
        // Future: untouched.
        Assert.Null(reminders[future.Id].FiredAt);
        Assert.Equal(future.RemindAt, reminders[future.Id].RemindAt);
    }
}
