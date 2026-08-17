using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when an intray move doesn't specify exactly one target — a group intray or a user intray (ADR 0532).
public sealed class IntrayMoveTargetRequiredException : IntrayException
{
    public IntrayMoveTargetRequiredException()
        : base("INTRAY_MOVE_TARGET_REQUIRED", StatusCodes.Status400BadRequest, "Specify exactly one move target — a group or a user.")
    {
    }
}
