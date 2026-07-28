using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Comments;

// Thrown when a reply's ParentCommentId isn't a top-level comment on this document (one-level threading, ADR
// "Document comment thread").
public sealed class InvalidParentCommentException : CommentException
{
    public InvalidParentCommentException()
        : base("INVALID_PARENT_COMMENT", StatusCodes.Status400BadRequest, "A reply's parent must be a top-level comment on this document.")
    {
    }
}
