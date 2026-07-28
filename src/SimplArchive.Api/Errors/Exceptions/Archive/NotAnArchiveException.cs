using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Archive;

// Thrown when archive-browsing is attempted on a document that isn't a .zip (ADR "Zip file browsing").
public sealed class NotAnArchiveException : ArchiveException
{
    public NotAnArchiveException()
        : base("NOT_AN_ARCHIVE", StatusCodes.Status400BadRequest, "This document is not a .zip archive.")
    {
    }
}
