using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

public sealed class InvalidCurrentPasswordException : PrincipalException
{
    public InvalidCurrentPasswordException()
        : base("INVALID_CURRENT_PASSWORD", StatusCodes.Status400BadRequest, "The current password is incorrect.")
    {
    }
}
