using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class InvalidLeafConflictException : DocumentException
{
    public InvalidLeafConflictException()
        : base("INVALID_LEAF_CONFLICT", StatusCodes.Status400BadRequest, "leafConflict must be rename, newVersion, or skip.")
    {
    }
}
