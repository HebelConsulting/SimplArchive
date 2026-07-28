using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Storage;

// Thrown when a new document blob would push a tenant past its storage quota (ADR "Per-tenant storage quota").
public sealed class StorageQuotaExceededException : StorageException
{
    public StorageQuotaExceededException(string message = "This operation would exceed the tenant's storage quota.")
        : base("STORAGE_QUOTA_EXCEEDED", StatusCodes.Status409Conflict, message)
    {
    }
}
