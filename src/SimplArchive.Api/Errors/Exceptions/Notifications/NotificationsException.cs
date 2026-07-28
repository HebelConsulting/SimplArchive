namespace SimplArchive.Api.Errors.Exceptions.Notifications;

// Base class for notification-preference errors (ADR "Notification preferences"). Inherits from ApiException so
// the global handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (NotificationsException)` for the whole area. See the exception-type principle in CLAUDE.md.
public abstract class NotificationsException : ApiException
{
    protected NotificationsException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
