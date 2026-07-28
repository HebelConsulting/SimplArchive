using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Storage;

// Thrown when a tenant's storage quota is set to a negative value (ADR "Per-tenant storage quota").
public sealed class InvalidStorageQuotaException : StorageException
{
    public InvalidStorageQuotaException()
        : base("INVALID_STORAGE_QUOTA", StatusCodes.Status400BadRequest, "The storage quota cannot be negative.")
    {
    }
}
