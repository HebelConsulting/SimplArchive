using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Users;

// Thrown when deactivating a user who still holds pending "In Review" workflow tasks and no replacement reviewer
// was supplied (ADR "Workflow review reassignment"). Deactivating them would orphan those reviews (a deactivated
// user gets no rights, so no one could act on the tasks), so the caller must hand them to a replacement via
// ?reassignReviewsTo=<userId>.
public sealed class ReviewerHasPendingReviewsException : UsersException
{
    public ReviewerHasPendingReviewsException(int pendingCount)
        : base("REVIEWER_HAS_PENDING_REVIEWS", StatusCodes.Status409Conflict,
            $"This user has {pendingCount} pending review task(s). Supply a replacement reviewer " +
            "(reassignReviewsTo) to hand them over before deactivating.")
    {
    }
}
