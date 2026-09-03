using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>The requested slot is malformed: start must precede end (ADR 0735's [start, end) semantics).</summary>
public sealed class BookingSlotInvalidException : BookingException
{
    public BookingSlotInvalidException(string detail)
        : base("BOOKING_SLOT_INVALID", StatusCodes.Status400BadRequest, detail)
    {
    }
}
