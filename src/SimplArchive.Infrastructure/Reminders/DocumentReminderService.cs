using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Reminders;

// Fires due document reminders (ADR "Document reminders"). Notifications are written directly (not via
// INotificationService, whose request-actor self-skip is meaningless for this system sweep), with each
// notification's TenantId taken from the reminder — like the workflow-escalation sweep. A one-shot reminder
// stamps FiredAt (done); a recurring one advances RemindAt to the next occurrence strictly after now (so a
// sweep that was down for several periods fires once and catches up, not once per missed period). Registered
// scoped; the hosted DocumentReminderWorker calls it on a timer.
public sealed class DocumentReminderService : IDocumentReminderService
{
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;

    public DocumentReminderService(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Pending reminders across all tenants (FiredAt == null). The RemindAt <= now comparison is done in
        // memory — SQLite can't translate a DateTimeOffset comparison in SQL (same as the escalation sweep).
        var pending = await _dbContext.DocumentReminders
            .IgnoreQueryFilters(TenantFilterOnly)
            .Where(r => r.FiredAt == null)
            .ToListAsync(cancellationToken);

        var acted = 0;
        foreach (var reminder in pending.Where(r => r.RemindAt <= now))
        {
            var documentName = await _dbContext.Documents
                .IgnoreQueryFilters(TenantFilterOnly)
                .Where(d => d.Id == reminder.DocumentId)
                .Select(d => d.Name)
                .SingleOrDefaultAsync(cancellationToken);

            // The document is gone (a hard delete cascades the reminder away, so this is a rare race) — retire
            // the reminder so it drops out of future scans.
            if (documentName is null)
            {
                reminder.FiredAt = now;
                acted++;
                continue;
            }

            // Name the setter when the reminder was assigned to someone else.
            var setByName = reminder.CreatedByUserId != reminder.UserId
                ? await _dbContext.Users.IgnoreQueryFilters(TenantFilterOnly)
                    .Where(u => u.Id == reminder.CreatedByUserId)
                    .Select(u => u.DisplayName)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;

            var body = $"Reminder for '{documentName}'.";
            if (!string.IsNullOrWhiteSpace(reminder.Note))
            {
                body += $" {reminder.Note}";
            }
            if (setByName is not null)
            {
                body += $" (set by {setByName})";
            }

            _dbContext.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                TenantId = reminder.TenantId,
                RecipientUserId = reminder.UserId,
                Type = NotificationType.DocumentReminder,
                Title = "Reminder",
                Body = body,
                DocumentId = reminder.DocumentId,
                CreatedAt = now,
            });

            if (reminder.Recurrence == ReminderRecurrence.None)
            {
                reminder.FiredAt = now;
            }
            else
            {
                reminder.RemindAt = NextOccurrence(reminder.RemindAt, reminder.Recurrence, now);
            }

            acted++;
        }

        if (acted > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return acted;
    }

    // The first occurrence strictly after now — fires once and schedules the next future occurrence, skipping
    // any missed periods rather than firing once per missed period.
    private static DateTimeOffset NextOccurrence(DateTimeOffset from, ReminderRecurrence recurrence, DateTimeOffset now)
    {
        var next = Advance(from, recurrence);
        while (next <= now)
        {
            next = Advance(next, recurrence);
        }

        return next;
    }

    private static DateTimeOffset Advance(DateTimeOffset value, ReminderRecurrence recurrence) => recurrence switch
    {
        ReminderRecurrence.Daily => value.AddDays(1),
        ReminderRecurrence.Weekly => value.AddDays(7),
        ReminderRecurrence.Monthly => value.AddMonths(1),
        _ => throw new ArgumentOutOfRangeException(nameof(recurrence), recurrence, "Not a recurring cadence."),
    };
}
