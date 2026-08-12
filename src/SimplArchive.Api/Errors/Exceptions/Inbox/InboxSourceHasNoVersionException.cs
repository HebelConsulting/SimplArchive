using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Inbox;

// Thrown when a document is copied into the inbox as a template but has no confirmed version to copy (#467).
public sealed class InboxSourceHasNoVersionException : InboxException
{
    public InboxSourceHasNoVersionException(string documentName)
        : base(
            "INBOX_SOURCE_HAS_NO_VERSION",
            StatusCodes.Status409Conflict,
            $"'{documentName}' has no version to copy into the inbox.")
    {
    }
}
