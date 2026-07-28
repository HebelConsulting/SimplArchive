using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Import;

// Thrown when the archive's formatVersion isn't one this build can import (ADR "Repository / folder import" /
// "Idempotent re-import").
public sealed class UnsupportedArchiveVersionException : ImportException
{
    public UnsupportedArchiveVersionException(int formatVersion)
        : base("UNSUPPORTED_ARCHIVE_VERSION", StatusCodes.Status400BadRequest, $"Unsupported archive format version {formatVersion}.")
    {
    }
}
