using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Notifications;

// A per-User, per-type email-notification preference (ADR "Notification preferences"). In-app notifications are
// always delivered; this only governs whether the EmailNotificationDispatcher also emails a given type. The
// absence of a row means the default (email enabled), so existing users need no backfill. Only the mutable
// types (see NotificationTypePolicy) are ever stored — the deadline/compliance escalations can't be muted.
public class UserNotificationPreference : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The User whose preference this is; an in-app inbox (and therefore an email) is per-User.
    public Guid UserId { get; set; }

    public NotificationType Type { get; set; }

    // Whether this type is also emailed for this user (in-app is unconditional). Default true = emailed.
    public bool EmailEnabled { get; set; } = true;
}
