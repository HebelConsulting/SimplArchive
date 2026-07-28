using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Workflow;

// Thrown when the reviewer chosen for a submit-for-review is not a valid target (ADR "Workflow engine — slice 1").
// Both sub-conditions share the INVALID_REVIEWER wire code (the client sees one error); the static factories keep
// the two throw sites reading intent-first while preserving each distinct detail message.
public sealed class InvalidReviewerException : WorkflowException
{
    private InvalidReviewerException(string message)
        : base("INVALID_REVIEWER", StatusCodes.Status400BadRequest, message)
    {
    }

    public static InvalidReviewerException NotActive() =>
        new("The reviewer does not exist or is not active.");

    public static InvalidReviewerException CannotReadContent() =>
        new("The reviewer cannot read this document's content.");

    public static InvalidReviewerException AlreadyAssigned() =>
        new("The review is already assigned to that reviewer.");
}
