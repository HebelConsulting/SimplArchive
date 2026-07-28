using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Archive;

// Thrown when an archive is too large to browse (the zip-bomb guard on the listing, ADR "Zip file browsing").
public sealed class ArchiveTooLargeException : ArchiveException
{
    public ArchiveTooLargeException()
        : base("ARCHIVE_TOO_LARGE", StatusCodes.Status400BadRequest, "The archive is too large to browse.")
    {
    }
}
