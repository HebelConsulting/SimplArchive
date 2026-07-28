using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Users;

// Thrown when the replacement reviewer supplied to a deactivation (?reassignReviewsTo=) is not a valid target
// (ADR "Workflow review reassignment"): it must be an active tenant User other than the one being deactivated.
// (Per-document read rights aren't individually enforced — the assigned reviewer gets gating access to the
// version by virtue of the assignment, ADR "Workflow status-gating".)
public sealed class InvalidReplacementReviewerException : UsersException
{
    public InvalidReplacementReviewerException()
        : base("INVALID_REPLACEMENT_REVIEWER", StatusCodes.Status400BadRequest,
            "The replacement reviewer does not exist, is not active, or is the user being deactivated.")
    {
    }
}
