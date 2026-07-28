using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class InvalidWormModeException : TenantException
{
    public InvalidWormModeException()
        : base("INVALID_WORM_MODE", StatusCodes.Status400BadRequest, "The WORM lock mode is not recognized.")
    {
    }
}
