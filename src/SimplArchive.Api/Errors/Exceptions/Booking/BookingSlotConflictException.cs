using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// The requested slot overlaps an existing Active booking of the same resource (ADR 0735).
/// </summary>
/// <remarks>
/// The Api translation of the DbContext's <c>BookingInvariantException.SlotTaken</c> — caught SPECIFICALLY
/// at the booking endpoints, never left to a blanket <c>InvalidOperationException</c> catch, which would
/// report a slot conflict as whatever that catch assumes (the blanket-catch-false-cause lesson). 409, the
/// same class of refusal as a name conflict: the request was well-formed, the world said no.
/// </remarks>
public sealed class BookingSlotConflictException : BookingException
{
    public BookingSlotConflictException(string detail)
        : base("BOOKING_SLOT_CONFLICT", StatusCodes.Status409Conflict, detail)
    {
    }
}
