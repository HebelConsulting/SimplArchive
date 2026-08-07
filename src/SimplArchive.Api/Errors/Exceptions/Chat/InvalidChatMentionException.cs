using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Chat;

// Thrown when a message's body mentions a user who is deactivated, or who cannot see this document (issue #383).
//
// Deliberately rejected rather than dropped: a mention subscribes the named user to the document and sends them
// a notification carrying its NAME, so accepting one for somebody without access would leak the document's
// existence and name to them. The message says which user, so a caller who lost a race with an ACL change can
// see what to remove.
public sealed class InvalidChatMentionException : ChatException
{
    public InvalidChatMentionException(Guid userId)
        : base("INVALID_CHAT_MENTION", StatusCodes.Status400BadRequest,
            $"Mentioned user {userId} is inactive or cannot see this document.")
    {
    }
}
