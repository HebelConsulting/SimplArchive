using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Inbox;

// Thrown when an inbox operation is missing the item's file name (ADR "S3-backed inbox").
public sealed class InboxFilenameRequiredException : InboxException
{
    public InboxFilenameRequiredException()
        : base("INBOX_FILENAME_REQUIRED", StatusCodes.Status400BadRequest, "A file name is required.")
    {
    }
}
