using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Import;

// Thrown when a version's blob in the archive fails its SHA-256 checksum on import (ADR "Repository / folder
// import").
public sealed class ArchiveBlobCorruptException : ImportException
{
    public ArchiveBlobCorruptException()
        : base("ARCHIVE_BLOB_CORRUPT", StatusCodes.Status400BadRequest, "A version's blob failed its checksum.")
    {
    }
}
