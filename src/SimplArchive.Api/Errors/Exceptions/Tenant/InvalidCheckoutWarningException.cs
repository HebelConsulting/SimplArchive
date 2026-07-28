using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class InvalidCheckoutWarningException : TenantException
{
    public InvalidCheckoutWarningException()
        : base("INVALID_CHECKOUT_WARNING", StatusCodes.Status400BadRequest, "Check-out warning days cannot be negative.")
    {
    }
}
