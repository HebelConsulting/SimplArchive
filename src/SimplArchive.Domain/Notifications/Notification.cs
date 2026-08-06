using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Notifications;

// The kind of in-app notification (ADR "Notifications (in-app, first slice)"). A fixed set for this slice —
// workflow transitions, a new comment/reply, and being granted access.
public enum NotificationType
{
    ReviewAssigned = 0,
    WorkflowApproved = 1,
    WorkflowRejected = 2,
    WorkflowReleased = 3,
    ChatMessagePosted = 4,
    AccessGranted = 5,
    ReviewReminder = 6,
    ReviewOverdue = 7,

    // A check-out was auto-released after sitting idle past the tenant's TTL (ADR "Stale check-out
    // auto-release sweep") — the former holder is told their lock (and stashed working copy) is gone.
    CheckoutExpired = 8,

    // A check-out is approaching its auto-release (ADR "Check-out expiry UX") — the holder is warned in the
    // grace window before the sweep releases it, so they can check in or keep working.
    CheckoutExpiring = 9,

    // The tenant's storage usage crossed a soft-quota warning threshold (ADR "Storage soft-quota warnings") —
    // the tenant's admins are warned before the hard quota starts rejecting uploads.
    StorageQuotaWarning = 10,

    // A document the user follows changed (ADR "Document subscriptions") — a new version, a new comment, or the
    // approval workflow reached Released. The Title/Body say which.
    SubscribedActivity = 11,

    // A user-set reminder (Wiedervorlage) on a document came due (ADR "Document reminders") — the target is
    // put back in front of the document, with the reminder's note.
    DocumentReminder = 12,
}

// A per-User in-app notification (ADR "Notifications (in-app, first slice)"). Written by INotificationService
// at the trigger sites (workflow / comment / ACL), read through the recipient's own inbox. ITenantScoped, so
// the tenant query filter applies. Title/Body are pre-rendered snapshots (like AuditEvent), so a later rename
// doesn't rewrite the message. Not versioned/editable; only ReadAt changes (mark-read).
public class Notification : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The User who receives it — in-app inboxes are per-User (a ServiceAccount has none).
    public Guid RecipientUserId { get; set; }

    public NotificationType Type { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    // How many events this (unread) notification represents (ADR "Notification digest / coalescing"). 1 for a
    // normal notification; incremented when a burst of activity on the same document coalesces into it while it's
    // unread, so the clients can render "… (×N)" instead of N separate rows. Reading the row ends the digest — the
    // next event starts a fresh notification.
    public int EventCount { get; set; } = 1;

    // The related document, for click-through navigation (null for a document-less notification).
    public Guid? DocumentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Null = unread; set = read at that instant.
    public DateTimeOffset? ReadAt { get; set; }

    // Email delivery bookkeeping (ADR "Email notifications (SMTP)"): null = not yet emailed; set to the send
    // instant once EmailNotificationWorker delivers it. A failed send leaves it null so the next sweep retries.
    public DateTimeOffset? EmailedAt { get; set; }
}
