using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class MoveTargetNotFoundException : DocumentException
{
    public MoveTargetNotFoundException()
        : base("MOVE_TARGET_NOT_FOUND", StatusCodes.Status404NotFound, "The target folder was not found.")
    {
    }
}
