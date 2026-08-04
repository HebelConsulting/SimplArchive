using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Inbox;

// Thrown when an inbox move doesn't specify exactly one target — a group inbox or a user inbox (ADR 0532).
public sealed class InboxMoveTargetRequiredException : InboxException
{
    public InboxMoveTargetRequiredException()
        : base("INBOX_MOVE_TARGET_REQUIRED", StatusCodes.Status400BadRequest, "Specify exactly one move target — a group or a user.")
    {
    }
}
