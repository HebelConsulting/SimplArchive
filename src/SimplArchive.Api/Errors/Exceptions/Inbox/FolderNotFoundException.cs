using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Inbox;

// Thrown when filing an inbox item into a folder that doesn't exist (ADR "S3-backed inbox").
public sealed class FolderNotFoundException : InboxException
{
    public FolderNotFoundException()
        : base("FOLDER_NOT_FOUND", StatusCodes.Status400BadRequest, "The target folder does not exist.")
    {
    }
}
