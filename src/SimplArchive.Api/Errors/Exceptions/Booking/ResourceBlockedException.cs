using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// The resource is out of service for part or all of the requested slot (ADR 0778).
/// </summary>
/// <remarks>
/// Its own code rather than a second use of <c>BOOKING_SLOT_CONFLICT</c>, because a client that cannot tell
/// the two apart gives the wrong advice: a taken slot means try another time, a blocked one means this
/// aircraft is not available at all and no time inside the block will do. The detail names the block's
/// window so the caller can see where the edge is.
///
/// 409 like the conflict it sits beside — the request was well-formed, the world said no.
/// </remarks>
public sealed class ResourceBlockedException : BookingException
{
    public ResourceBlockedException(string detail)
        : base("RESOURCE_BLOCKED", StatusCodes.Status409Conflict, detail)
    {
    }
}
