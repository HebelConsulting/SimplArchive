using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class TenantNameRequiredException : TenantException
{
    public TenantNameRequiredException()
        : base("TENANT_NAME_REQUIRED", StatusCodes.Status400BadRequest, "The tenant name is required.")
    {
    }
}
