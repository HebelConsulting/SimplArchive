using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Chat;

// Thrown when a comment/reply body is blank (ADR "Document comment thread").
public sealed class EmptyChatMessageException : ChatException
{
    public EmptyChatMessageException()
        : base("EMPTY_CHAT_MESSAGE", StatusCodes.Status400BadRequest, "A chat message cannot be empty.")
    {
    }
}
