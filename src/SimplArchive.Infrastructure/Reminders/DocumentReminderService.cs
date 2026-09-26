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
            // ONE reminder, ONE transaction — not one transaction for the whole batch. Each reminder is its own
            // unit of work, so a failure on one neither rolls back the others nor holds a row lock across them
            // (which is what a batch-wide transaction would do, and how two sweeps would deadlock).
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (!await ClaimAsync(reminder, now, cancellationToken))
            {
                // Another sweep won this reminder. Not an error and not worth a log line: it is the normal
                // outcome of two instances sweeping, which is the arrangement (ADR 0808).
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            var documentName = await _dbContext.Documents
                .IgnoreQueryFilters(TenantFilterOnly)
                .Where(d => d.Id == reminder.DocumentId)
                .Select(d => d.Name)
                .SingleOrDefaultAsync(cancellationToken);

            // The document is gone (a hard delete cascades the reminder away, so this is a rare race) — the
            // claim above already retired the reminder, so it simply drops out of future scans with no
            // notification. Committing matters: rolling back here would resurrect it for the next sweep.
            if (documentName is null)
            {
                await transaction.CommitAsync(cancellationToken);
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

            // No marker is written here: the CLAIM above already did it, atomically, which is the whole point.
            // Writing it again would be a second update to a row this context no longer holds accurate state
            // for — the ChangeTracker cannot see an ExecuteUpdate.
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            acted++;
        }

        return acted;
    }

    /// <summary>
    /// Takes this reminder for THIS sweep, atomically, and says whether it was won.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A compare-and-swap on the marker that was read: the one-shot's <c>FiredAt</c> must still be null, and
    /// the recurring reminder's <c>RemindAt</c> must still be the value this sweep saw. `ExecuteUpdate` is one
    /// statement, so the database decides the winner and the loser gets 0 rows.
    /// </para>
    /// <para>
    /// <b>Why this exists</b> (#1425): the sweep used to read every pending reminder, build its notifications
    /// and save once at the end. That is the correct transactional shape (ADR 0794) and it has no exclusion —
    /// two sweeps both read <c>FiredAt == null</c> before either committed, so both acted. Measured, not
    /// feared: two concurrent sweeps against one one-shot reminder produced <c>acted=[1,1]</c> and
    /// <b>two notifications</b>, first try. And it is not a test artefact — ADR 0808 runs two app instances,
    /// each registering this worker on its own 60-second timer, so the overlap is the arrangement rather than
    /// an accident. The same family as the connection-pool ceiling that multiplied by instance count (#750).
    /// </para>
    /// <para>
    /// A row-level claim rather than one instance owning the sweep: both instances keep working and split the
    /// batch, and it stays correct at any instance count — including one nobody planned for.
    /// </para>
    /// </remarks>
    private async Task<bool> ClaimAsync(
        DocumentReminder reminder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = _dbContext.DocumentReminders
            .IgnoreQueryFilters(TenantFilterOnly)
            .Where(r => r.Id == reminder.Id);

        var won = reminder.Recurrence == ReminderRecurrence.None
            ? await rows.Where(r => r.FiredAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.FiredAt, (DateTimeOffset?)now), cancellationToken)
            : await rows.Where(r => r.RemindAt == reminder.RemindAt)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(
                        r => r.RemindAt, NextOccurrence(reminder.RemindAt, reminder.Recurrence, now)),
                    cancellationToken);

        return won == 1;
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
