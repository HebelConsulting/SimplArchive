using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class InvalidMoveTargetException : DocumentException
{
    public InvalidMoveTargetException()
        : base("INVALID_MOVE_TARGET", StatusCodes.Status400BadRequest, "Cannot move an item into itself or one of its own descendants.")
    {
    }
}
