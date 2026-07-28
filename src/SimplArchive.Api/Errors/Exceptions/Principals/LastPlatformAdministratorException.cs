using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

public sealed class LastPlatformAdministratorException : PrincipalException
{
    public LastPlatformAdministratorException()
        : base("LAST_PLATFORM_ADMINISTRATOR", StatusCodes.Status409Conflict, "Cannot revoke the last active platform administrator.")
    {
    }
}
