using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

public sealed class ServiceAccountNameConflictException : PrincipalException
{
    public ServiceAccountNameConflictException()
        : base("SERVICE_ACCOUNT_NAME_CONFLICT", StatusCodes.Status409Conflict, "A service account with this name already exists.")
    {
    }
}
