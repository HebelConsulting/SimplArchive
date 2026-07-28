using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Workflow;

// Thrown when a workflow action isn't valid for the version's current status, e.g. approving a Draft (ADR
// "Workflow engine — slice 1"). The message is built by the caller from the current status + attempted action.
public sealed class InvalidWorkflowTransitionException : WorkflowException
{
    public InvalidWorkflowTransitionException(string message)
        : base("INVALID_WORKFLOW_TRANSITION", StatusCodes.Status409Conflict, message)
    {
    }
}
