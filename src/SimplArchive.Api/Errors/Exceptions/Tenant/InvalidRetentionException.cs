using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class InvalidRetentionException : TenantException
{
    public InvalidRetentionException()
        : base("INVALID_RETENTION", StatusCodes.Status400BadRequest, "Audit retention days cannot be negative.")
    {
    }
}
