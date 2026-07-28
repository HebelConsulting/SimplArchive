using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

public sealed class GroupNotEmptyException : PrincipalException
{
    public GroupNotEmptyException()
        : base("GROUP_NOT_EMPTY", StatusCodes.Status409Conflict, "The group still has child groups or members.")
    {
    }
}
