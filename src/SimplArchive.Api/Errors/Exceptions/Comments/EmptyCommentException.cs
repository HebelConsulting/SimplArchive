using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Comments;

// Thrown when a comment/reply body is blank (ADR "Document comment thread").
public sealed class EmptyCommentException : CommentException
{
    public EmptyCommentException()
        : base("EMPTY_COMMENT", StatusCodes.Status400BadRequest, "A comment cannot be empty.")
    {
    }
}
