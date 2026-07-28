using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class AdministratorEmailConflictException : TenantException
{
    public AdministratorEmailConflictException()
        : base("ADMINISTRATOR_EMAIL_CONFLICT", StatusCodes.Status409Conflict, "A user with this email already exists.")
    {
    }
}
