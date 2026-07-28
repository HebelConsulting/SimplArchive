using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Notifications;

// A notification-preference update referenced a type that isn't user-mutable (ADR "Notification preferences"):
// either an undefined NotificationType value, or one of the deadline/compliance escalations that are always
// emailed. Both share the INVALID_NOTIFICATION_PREFERENCE wire code.
public sealed class InvalidNotificationPreferenceException : NotificationsException
{
    public InvalidNotificationPreferenceException()
        : base("INVALID_NOTIFICATION_PREFERENCE", StatusCodes.Status400BadRequest,
            "One or more notification types cannot have their email channel changed.")
    {
    }
}
