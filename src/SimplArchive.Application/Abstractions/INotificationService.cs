using SimplArchive.Domain.Notifications;

namespace SimplArchive.Application.Abstractions;

// Creates an in-app notification for a recipient User at a trigger site (workflow / comment / ACL) — see ADR
// "Notifications (in-app, first slice)". Best-effort, like the audit recorder: it never notifies the actor
// about their own action, and Title/Body are pre-rendered. A call with no tenant in scope is DROPPED —
// and says so at Warning (ADR 0626), because a notification that silently does not arrive is
// indistinguishable from one nobody sent; NotifyInTenantAsync is the way to name the tenant instead.
public interface INotificationService
{
    Task NotifyAsync(
        Guid recipientUserId,
        NotificationType type,
        string title,
        string body,
        Guid? documentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies a recipient in an EXPLICITLY named tenant, for callers that have no ambient one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ambient overload above reads <c>ICurrentTenantAccessor</c> and does nothing when it is unset —
    /// which is right for a request but wrong for a worker, a protocol edge, or a document finalized later,
    /// where no tenant is in scope and the notification is simply dropped. <c>IAuditRecorder</c> already takes
    /// an explicit tenant for exactly this reason; this is the same seam for the same failure.
    /// </para>
    /// <para>
    /// An overload rather than an added parameter because all 25 existing call sites pass their
    /// <see cref="CancellationToken"/> positionally: inserting a parameter would churn every one of them, and
    /// appending one after the token is the shape analyzers reject.
    /// </para>
    /// </remarks>
    Task NotifyInTenantAsync(
        Guid tenantId,
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
