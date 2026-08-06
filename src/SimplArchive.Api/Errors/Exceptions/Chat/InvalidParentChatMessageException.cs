using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Chat;

// Thrown when a reply's ParentMessageId isn't a top-level comment on this document (one-level threading, ADR
// "Document comment thread").
public sealed class InvalidParentChatMessageException : ChatException
{
    public InvalidParentChatMessageException()
        : base("INVALID_PARENT_CHAT_MESSAGE", StatusCodes.Status400BadRequest, "A reply's parent must be a top-level message on this document.")
    {
    }
}
