using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Reminders;

// Thrown when a reminder's due date isn't in the future (ADR "Document reminders").
public sealed class ReminderInPastException : ReminderException
{
    public ReminderInPastException()
        : base("REMINDER_IN_PAST", StatusCodes.Status400BadRequest, "A reminder's due date must be in the future.")
    {
    }
}
