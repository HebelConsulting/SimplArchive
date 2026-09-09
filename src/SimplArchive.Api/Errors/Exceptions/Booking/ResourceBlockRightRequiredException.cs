using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// Taking a resource out of service needs <c>CanBlockResources</c> (ADR 0778).
/// </summary>
/// <remarks>
/// Its own right rather than tenant administration, by owner decision: grounding an aircraft is the head of
/// maintenance's act, and gating it on <c>IsTenantAdmin</c> would force a maintenance organisation to hand
/// out full tenant administration to grant it.
///
/// 403 rather than 404: the caller may well see the resource and its Maintenance collection perfectly well.
/// What they lack is the authority to withdraw it, and saying so is more useful than pretending the
/// collection is not there.
/// </remarks>
public sealed class ResourceBlockRightRequiredException : BookingException
{
    public ResourceBlockRightRequiredException(string detail)
        : base("BLOCK_RIGHT_REQUIRED", StatusCodes.Status403Forbidden, detail)
    {
    }
}
