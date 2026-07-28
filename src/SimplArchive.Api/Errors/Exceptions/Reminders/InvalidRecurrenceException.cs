using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Reminders;

// Thrown when a reminder's recurrence isn't a defined value (ADR "Document reminders").
public sealed class InvalidRecurrenceException : ReminderException
{
    public InvalidRecurrenceException()
        : base("INVALID_RECURRENCE", StatusCodes.Status400BadRequest, "The reminder recurrence is not a valid value.")
    {
    }
}
