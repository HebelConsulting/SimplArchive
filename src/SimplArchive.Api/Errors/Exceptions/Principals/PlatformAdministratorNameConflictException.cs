using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

public sealed class PlatformAdministratorNameConflictException : PrincipalException
{
    public PlatformAdministratorNameConflictException()
        : base("PLATFORM_ADMINISTRATOR_NAME_CONFLICT", StatusCodes.Status409Conflict, "A platform administrator with this name already exists.")
    {
    }
}
