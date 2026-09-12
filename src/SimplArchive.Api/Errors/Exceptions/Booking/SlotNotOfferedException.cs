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
    /// <param name="offered">
    /// What the resource DOES offer around the requested slot, as DATA (#1135) — so a client can say "only
    /// available 08:00–22:00" in the reader's own language instead of quoting this exception's English
    /// message, which it must never do (issue #424).
    /// </param>
    public SlotNotOfferedException(string detail, IReadOnlyList<SimplArchive.Domain.Booking.SlotOccurrence>? offered = null)
        : base("SLOT_NOT_OFFERED", StatusCodes.Status409Conflict, detail, Extension(offered))
    {
    }

    /// <summary>An empty list is omitted: "offers nothing that day" is a different sentence from "not then".</summary>
    private static IReadOnlyDictionary<string, object?>? Extension(
        IReadOnlyList<SimplArchive.Domain.Booking.SlotOccurrence>? offered) =>
        offered is { Count: > 0 }
            ? new Dictionary<string, object?>
            {
                ["offered"] = offered
                    .Select(window => new { startsAt = window.StartsAtUtc, endsAt = window.EndsAtUtc })
                    .ToList(),
            }
            : null;
}
