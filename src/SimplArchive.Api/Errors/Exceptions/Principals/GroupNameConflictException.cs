using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

public sealed class GroupNameConflictException : PrincipalException
{
    public GroupNameConflictException()
        : base("GROUP_NAME_CONFLICT", StatusCodes.Status409Conflict, "A group with this name already exists under the same parent.")
    {
    }
}
