using SimplArchive.Domain.Notifications;

namespace SimplArchive.Application.Abstractions;

// Creates an in-app notification for a recipient User at a trigger site (workflow / comment / ACL) — see ADR
// "Notifications (in-app, first slice)". Best-effort, like the audit recorder: it no-ops when there's no
// current tenant, and it never notifies the actor about their own action. Title/Body are pre-rendered.
public interface INotificationService
{
    Task NotifyAsync(
        Guid recipientUserId,
        NotificationType type,
        string title,
        string body,
        Guid? documentId = null,
        CancellationToken cancellationToken = default);

    // Notifies every user subscribed to (following) the document (ADR "Document subscriptions"), except the
    // acting user and anyone in excludeUserIds (recipients already notified by the primary trigger, so they
    // aren't notified twice for the same event). Best-effort, in one commit; no-ops when there's no tenant.
    Task NotifyDocumentSubscribersAsync(
        Guid documentId,
        NotificationType type,
        string title,
        string body,
        IEnumerable<Guid>? excludeUserIds = null,
        CancellationToken cancellationToken = default);
}
