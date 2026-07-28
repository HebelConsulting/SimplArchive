namespace SimplArchive.Api.Errors.Exceptions.Reminders;

// Base class for document-reminder errors (ADR "Document reminders"). Inherits from ApiException so the global
// handler translates it to RFC 7807; concrete errors inherit from this so a caller can `catch (ReminderException)`.
public abstract class ReminderException : ApiException
{
    protected ReminderException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
