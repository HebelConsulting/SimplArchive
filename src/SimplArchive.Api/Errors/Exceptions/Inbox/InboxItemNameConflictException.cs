using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Inbox;

// Thrown when the inbox already holds an item of that name — the inbox is addressed BY NAME, so a second one
// would overwrite the first (#467).
public sealed class InboxItemNameConflictException : InboxException
{
    public InboxItemNameConflictException(string name)
        : base(
            "INBOX_ITEM_NAME_CONFLICT",
            StatusCodes.Status409Conflict,
            $"Your inbox already holds an item named '{name}'.")
    {
    }
}
