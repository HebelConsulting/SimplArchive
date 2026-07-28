using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

public sealed class UserEmailConflictException : PrincipalException
{
    public UserEmailConflictException()
        : base("USER_EMAIL_CONFLICT", StatusCodes.Status409Conflict, "A user with this email already exists.")
    {
    }
}
