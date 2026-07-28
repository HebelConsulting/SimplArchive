using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Storage;

// Thrown when a tenant's incomplete-upload-cleanup lifecycle setting is negative (ADR "Per-tenant bucket policy
// knobs").
public sealed class InvalidUploadCleanupException : StorageException
{
    public InvalidUploadCleanupException()
        : base("INVALID_UPLOAD_CLEANUP", StatusCodes.Status400BadRequest, "Incomplete-upload cleanup days cannot be negative.")
    {
    }
}
