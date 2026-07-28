using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Archive;

// Thrown when a single archive entry exceeds the zip-bomb guard (ADR "Zip file browsing"). The message carries
// the guard's detail.
public sealed class ArchiveEntryTooLargeException : ArchiveException
{
    public ArchiveEntryTooLargeException(string message)
        : base("ARCHIVE_ENTRY_TOO_LARGE", StatusCodes.Status400BadRequest, message)
    {
    }
}
