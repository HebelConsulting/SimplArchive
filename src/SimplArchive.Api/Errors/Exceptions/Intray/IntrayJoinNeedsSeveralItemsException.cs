using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when a join names fewer than two items (#487) — joining one thing is a copy, and joining none is
// nothing. The client's Join action is enabled only on a multiple selection, so this guards the API itself.
public sealed class IntrayJoinNeedsSeveralItemsException : IntrayException
{
    public IntrayJoinNeedsSeveralItemsException()
        : base(
            "INTRAY_JOIN_NEEDS_SEVERAL_ITEMS",
            StatusCodes.Status400BadRequest,
            "Joining needs at least two intray items.")
    {
    }
}
