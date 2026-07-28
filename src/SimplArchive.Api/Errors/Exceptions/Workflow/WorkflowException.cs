namespace SimplArchive.Api.Errors.Exceptions.Workflow;

// Base class for approval-workflow errors (ADR "Workflow engine — slice 1"). Inherits from ApiException so the
// global handler translates it to an RFC 7807 response; concrete errors (e.g. InvalidReviewerException) inherit
// from this so a caller can `catch (WorkflowException)` for the whole area. See the exception-type principle in
// CLAUDE.md.
public abstract class WorkflowException : ApiException
{
    protected WorkflowException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
