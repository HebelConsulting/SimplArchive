using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Workflow;

// Thrown when a version that is not yet Confirmed is submitted for review (ADR "Workflow engine — slice 1").
public sealed class VersionNotConfirmedException : WorkflowException
{
    public VersionNotConfirmedException()
        : base("VERSION_NOT_CONFIRMED", StatusCodes.Status400BadRequest, "Only a confirmed version can be submitted for review.")
    {
    }
}
