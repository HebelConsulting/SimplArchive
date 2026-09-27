using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Notifications;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Enqueues an <see cref="EmailOutbox"/> row for every notification being INSERTED, at the one door every write
/// path uses (ADR 0840).
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than at the six creation sites</b>, and that is the whole reason this shape was chosen over an
/// explicit enqueue service. <c>SearchIndexOutbox</c> and <c>SearchablePdfOutbox</c> are both enqueued by
/// callers through a <c>*OutboxQueue</c> service, so this departs from their precedent — deliberately, because
/// the two failures are not alike. A forgotten search enqueue leaves a document unindexed, which somebody
/// notices by searching for it. A forgotten EMAIL enqueue sends no mail, silently, to somebody who does not know
/// an email was owed: the same silent-omission argument that makes a missing capability flag worse than a
/// missing rel.
/// </para>
/// <para>
/// Five of the six sites <c>Add</c> to <c>_dbContext.Notifications</c> directly rather than going through
/// <c>NotificationService</c>, so there is no funnel to put this in short of <c>SaveChanges</c> — which is this
/// codebase's stated sole enforcement point anyway, and where the cycle, sibling-name and concurrency-token
/// invariants already live. A seventh creation site added later gets its email for free, which is the property
/// worth having.
/// </para>
/// <para>
/// <b>It runs BEFORE the save</b>, so the notification and its queue row commit in ONE transaction (ADR 0794).
/// An enqueue after the commit could be lost between the two writes, which is the outbox pattern's entire
/// failure mode; an enqueue before it cannot.
/// </para>
/// <para>
/// <b>No type filter</b>, matching what the old pending query did: every notification is a candidate, and the
/// recipient's per-type email preference is applied at DRAIN rather than here. That is not tidiness — a
/// preference read at enqueue time would be the preference as it was when the event happened, and a reader who
/// mutes a type should not then receive mail queued minutes earlier.
/// </para>
/// </remarks>
public static class EmailOutboxEnqueuer
{
    public static void Enqueue(DbContext dbContext, ChangeTracker changeTracker)
    {
        // MATERIALISED FIRST. Adding to the context mutates the ChangeTracker, and enumerating it while it
        // changes throws — so the list of subjects is taken before a single row is added.
        var added = changeTracker.Entries<Notification>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            // A notification INSERTED already claiming to be handled owes no email. No production path writes
            // that state — every creation site leaves both timestamps null — so this is a guard rather than
            // load-bearing, and it is here because the alternative is a spurious send: an import, a backfill or a
            // fixture that carries historical notifications would otherwise email every one of them on the next
            // sweep. Cheap, and the failure it prevents is one nobody could undo.
            .Where(n => n.EmailedAt is null && n.EmailFailedAt is null)
            .ToList();

        if (added.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var notification in added)
        {
            dbContext.Set<EmailOutbox>().Add(new EmailOutbox
            {
                Id = Guid.NewGuid(),
                NotificationId = notification.Id,
                TenantId = notification.TenantId,
                EnqueuedAt = now,
                State = EmailOutboxState.Pending,
            });
        }
    }
}
