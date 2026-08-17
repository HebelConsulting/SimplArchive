using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when the intray already holds an item of that name — the intray is addressed BY NAME, so a second one
// would overwrite the first (#467).
public sealed class IntrayItemNameConflictException : IntrayException
{
    public IntrayItemNameConflictException(string name)
        : base(
            "INTRAY_ITEM_NAME_CONFLICT",
            StatusCodes.Status409Conflict,
            $"Your intray already holds an item named '{name}'.")
    {
    }
}
