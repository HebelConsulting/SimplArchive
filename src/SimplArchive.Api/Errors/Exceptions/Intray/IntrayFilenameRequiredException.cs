using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when an intray operation is missing the item's file name (ADR "S3-backed inbox").
public sealed class IntrayFilenameRequiredException : IntrayException
{
    public IntrayFilenameRequiredException()
        : base("INTRAY_FILENAME_REQUIRED", StatusCodes.Status400BadRequest, "A file name is required.")
    {
    }
}
