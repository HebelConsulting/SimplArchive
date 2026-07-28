using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Workflow;

// Thrown when a reject transition carries no reason (ADR "Workflow engine — slice 1"; the reason is stored on the
// transition record, ADR 0143).
public sealed class RejectionReasonRequiredException : WorkflowException
{
    public RejectionReasonRequiredException()
        : base("REJECTION_REASON_REQUIRED", StatusCodes.Status400BadRequest, "A reason is required to reject a document.")
    {
    }
}
