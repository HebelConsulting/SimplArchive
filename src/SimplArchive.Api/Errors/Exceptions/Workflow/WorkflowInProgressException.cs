using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Workflow;

// Thrown when an operation is refused because the document has an approval workflow in progress — a version
// In Review or Approved but not yet Released (ADR "Version restore"). E.g. restoring an old version can't
// quietly supersede a version under review.
public sealed class WorkflowInProgressException : WorkflowException
{
    public WorkflowInProgressException()
        : base("WORKFLOW_IN_PROGRESS", StatusCodes.Status409Conflict, "The document has an approval workflow in progress; release or cancel it first.")
    {
    }
}
