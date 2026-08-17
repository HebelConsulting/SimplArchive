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

    // Somebody addressed the user by name in a chat message (issue #383). Deliberately NOT coalescable, unlike
    // ChatMessagePosted: being addressed personally is a discrete, actionable thing, and folding it into a
    // "3 new comments" digest is exactly how a direct request gets missed.
    ChatMentioned = 13,
}

// A per-User in-app notification (ADR "Notifications (in-app, first slice)"). Written by INotificationService
// at the trigger sites (workflow / comment / ACL), read through the recipient's own intray. ITenantScoped, so
// the tenant query filter applies. Title/Body are pre-rendered snapshots (like AuditEvent), so a later rename
// doesn't rewrite the message. Not versioned/editable; only ReadAt changes (mark-read).
public class Notification : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The User who receives it — in-app intrayes are per-User (a ServiceAccount has none).
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

    /// <summary>
    /// How many times emailing this notification has been attempted and failed (ADR 0612). A failed send
    /// deliberately leaves <see cref="EmailedAt"/> null so the next sweep retries — at-least-once — but without
    /// a count there is nothing to distinguish "the server was down for a minute" from "this address cannot
    /// receive mail", and the second kind never leaves the pending set.
    /// </summary>
    public int EmailAttempts { get; set; }

    /// <summary>
    /// When the system gave up emailing this notification: the retry budget was spent, or the server rejected
    /// the address permanently. Set means it leaves the pending set for good — which is the point, because a
    /// bounded batch made entirely of hopeless rows stalls every legitimate notification behind it. The in-app
    /// notification is unaffected; only the email is abandoned.
    /// </summary>
    public DateTimeOffset? EmailFailedAt { get; set; }
}
