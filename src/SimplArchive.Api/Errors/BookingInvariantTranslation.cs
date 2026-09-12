using SimplArchive.Api.Errors.Exceptions.Booking;
using SimplArchive.Domain.Booking;

// Deliberately NOT under Errors/Exceptions: everything in that namespace must BE an ApiException
// (ArchitectureTests), and this is the map from a domain refusal to one — a neighbour of the
// exceptions, not one of them.
namespace SimplArchive.Api.Errors;

/// <summary>
/// The one translation from a DbContext booking invariant to its Api refusal (ADRs 0735/0778).
/// </summary>
/// <remarks>
/// <para>
/// Stated once because it was stated twice, and the copies did not agree. The bookings endpoint caught
/// <c>when (ex.Kind == BookingInvariantKind.SlotTaken)</c> — correct while that was the only refusal a
/// booking write could produce — so when ADR 0778 added <c>ResourceBlocked</c>, a booking made into a
/// maintenance block escaped as an untranslated <see cref="InvalidOperationException"/> and answered
/// <b>500</b>. The classifier, meanwhile, had a full switch and answered 409 for the same act.
/// </para>
/// <para>
/// The failure mode is what makes this worth a class: a narrow <c>when</c> clause does not fail when a new
/// kind arrives, it silently stops matching. Every kind now has to be named here, and the default is the
/// conservative one rather than a fall-through to nothing.
/// </para>
/// </remarks>
internal static class BookingInvariantTranslation
{
    /// <summary>The Api exception for an invariant refusal, chosen by KIND — never by matching message text.</summary>
    internal static ApiException Translate(BookingInvariantException error) => error.Kind switch
    {
        BookingInvariantKind.SlotTaken => new BookingSlotConflictException(error.Message),
        BookingInvariantKind.SlotWithoutExtent => new BookingSlotInvalidException(error.Message),
        BookingInvariantKind.BlockWithoutExtent => new BookingSlotInvalidException(error.Message),
        BookingInvariantKind.WindowWithoutExtent => new BookingSlotInvalidException(error.Message),
        // Its own code: a taken slot means try another hour, a blocked resource means no hour inside the
        // block will do, and a caller that cannot tell them apart gives the wrong advice (ADR 0778).
        BookingInvariantKind.ResourceBlocked => new ResourceBlockedException(error.Message),
        // ...and its own code again, for the same reason: "not on offer then" is a different sentence from
        // "out of service" and from "not bookable at all", and they have three different remedies (#1124).
        BookingInvariantKind.NotOffered => new SlotNotOfferedException(error.Message),
        BookingInvariantKind.ClaimsDisagreeOnSlot => new BookingSlotInvalidException(error.Message),
        _ => new ResourceNotBookableException(error.Message),
    };
}
