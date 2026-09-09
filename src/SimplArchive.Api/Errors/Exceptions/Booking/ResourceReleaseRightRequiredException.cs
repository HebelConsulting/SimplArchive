using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// Returning a resource to service needs <c>CanReleaseResources</c> (ADR 0778).
/// </summary>
/// <remarks>
/// Separate from <see cref="ResourceBlockRightRequiredException"/> because releasing is a different
/// authority, not a stronger form of the same one: grounding on suspicion should be broad, while "this
/// aircraft is airworthy again" is a certifying act. A single right could not express that, which is why
/// there are two — and why the audit actions are two as well.
///
/// A SERVICE ACCOUNT always lands here (owner decision 2026-09-09): there is no
/// <c>CanReleaseResources</c> column on a service account at all, so a machine may ground a resource but
/// never clear it. Nothing in the model could tell a considered release from a bug in an integration.
/// </remarks>
public sealed class ResourceReleaseRightRequiredException : BookingException
{
    public ResourceReleaseRightRequiredException(string detail)
        : base("RELEASE_RIGHT_REQUIRED", StatusCodes.Status403Forbidden, detail)
    {
    }
}
