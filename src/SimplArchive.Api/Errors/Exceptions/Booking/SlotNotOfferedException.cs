using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// The requested slot is not covered by any window the resource has offered (#1124).
/// </summary>
/// <remarks>
/// Its own code rather than a second use of <c>RESOURCE_BLOCKED</c> or <c>RESOURCE_NOT_BOOKABLE</c>, because
/// all three refuse for different reasons and a client that cannot tell them apart gives the wrong advice:
/// <list type="bullet">
/// <item>not bookable — this is not a resource you can book at all, ever;</item>
/// <item>blocked — it is out of service for this slot, and no time inside the block will do;</item>
/// <item>not offered — it is simply not on offer then; book inside a published window, or publish one.</item>
/// </list>
/// Only a resource that has published at least one window can refuse this way: one that has published none
/// books freely, exactly as before the rule existed.
///
/// 409 like the refusals it sits beside — the request was well-formed, the world said no.
/// </remarks>
public sealed class SlotNotOfferedException : BookingException
{
    public SlotNotOfferedException(string detail)
        : base("SLOT_NOT_OFFERED", StatusCodes.Status409Conflict, detail)
    {
    }
}
