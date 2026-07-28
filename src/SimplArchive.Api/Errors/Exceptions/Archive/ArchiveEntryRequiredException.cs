using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Archive;

// Thrown when the archive-entry content route is called without a ?path= entry (ADR "Zip file browsing").
public sealed class ArchiveEntryRequiredException : ArchiveException
{
    public ArchiveEntryRequiredException()
        : base("ARCHIVE_ENTRY_REQUIRED", StatusCodes.Status400BadRequest, "A ?path= entry is required.")
    {
    }
}
