using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>A recurring event was written into a Schedule (ADR 0744): a booking models ONE slot.</summary>
/// <remarks>
/// An RRULE would claim its first occurrence in the <c>ResourceBooking</c> row while the calendar showed
/// every repetition — a conflict check that cannot see what the eye sees. Refused with the reason named,
/// on every path (the booking endpoint composes no recurrence, so this fires on DAV and upload writes).
/// </remarks>
public sealed class BookingRecurrenceUnsupportedException : BookingException
{
    public BookingRecurrenceUnsupportedException()
        : base("BOOKING_RECURRENCE_UNSUPPORTED", StatusCodes.Status400BadRequest,
            "A booking cannot recur: a Schedule entry claims one slot, and a repeating rule would show "
            + "time the conflict check does not guard. File each occurrence as its own booking.")
    {
    }
}
