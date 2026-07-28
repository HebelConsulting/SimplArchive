using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class InvalidCheckoutTtlException : TenantException
{
    public InvalidCheckoutTtlException()
        : base("INVALID_CHECKOUT_TTL", StatusCodes.Status400BadRequest, "Check-out expiry days cannot be negative.")
    {
    }
}
