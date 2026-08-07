namespace SimplArchive.Domain.Notifications;

// Which notification types a user may mute the email channel for (ADR "Notification preferences"). The
// deadline/compliance escalations are deliberately excluded — a user shouldn't be able to silently miss an
// overdue-review or check-out-expiry alert — so they are always emailed regardless of preferences.
//
// The classification is an exhaustive switch (single source of truth per type), not a hand-maintained list
// (ADR "Document reminders" folded this in): adding a new NotificationType without a case here fails the
// build (CS8509 — a missing named enum member), so a new type can't silently default to "always emailed".
// The Mutable set is derived from it, so the preferences endpoint and its tests can never drift out of sync.
public static class NotificationTypePolicy
{
    public static bool IsMutable(NotificationType type) =>
#pragma warning disable CS8524 // No arm for unnamed/out-of-range enum values — those never occur (a runtime
        // SwitchExpressionException would be a bug); suppressing only CS8524 keeps CS8509 (a *named* member
        // with no case) as a build error, which is the gate that forces every new type to be classified here.
        type switch
        {
            NotificationType.ReviewAssigned => true,
            NotificationType.WorkflowApproved => true,
            NotificationType.WorkflowRejected => true,
            NotificationType.WorkflowReleased => true,
            NotificationType.ChatMessagePosted => true,
            // Mutable like the rest of the collaboration types: being mentioned is not a deadline or a
            // compliance escalation, so a user may switch its email off. It stays un-coalescable regardless —
            // muting the EMAIL channel is a different thing from folding the in-app row into a digest.
            NotificationType.ChatMentioned => true,
            NotificationType.AccessGranted => true,
            NotificationType.SubscribedActivity => true,
            NotificationType.DocumentReminder => true,

            NotificationType.ReviewReminder => false,
            NotificationType.ReviewOverdue => false,
            NotificationType.CheckoutExpired => false,
            NotificationType.CheckoutExpiring => false,
            NotificationType.StorageQuotaWarning => false,
        };
#pragma warning restore CS8524

    // The mutable types, derived from IsMutable so there's one source of truth (used by the preferences API).
    public static readonly IReadOnlyList<NotificationType> Mutable =
        [.. Enum.GetValues<NotificationType>().Where(IsMutable)];
}
