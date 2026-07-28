using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Reminders;

// Thrown when the reminder's target isn't an active user in the tenant who can see the document (ADR
// "Document reminders").
public sealed class InvalidReminderTargetException : ReminderException
{
    public InvalidReminderTargetException()
        : base("INVALID_REMINDER_TARGET", StatusCodes.Status400BadRequest, "The reminder target must be an active user who can see the document.")
    {
    }
}
