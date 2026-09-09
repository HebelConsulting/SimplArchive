using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// An <c>ATTENDEE</c> on a booking names somebody the archive cannot turn into a claim (ADR 0776).
/// </summary>
/// <remarks>
/// <para>
/// Refused rather than ignored, and the whole write with it. Dropping the attendee silently would book the
/// resource alone while the person who wrote the invitation believes a second participant is coming — a
/// booking that looks complete to everyone and is not. Serving something else is worse than refusing.
/// </para>
/// <para>
/// The detail names the address, because the two causes need different actions from the caller and only they
/// can tell which applies: the address belongs to nobody here, or it belongs to somebody who has no bookable
/// resource standing for them.
/// </para>
/// </remarks>
public sealed class BookingAttendeeUnknownException : BookingException
{
    public BookingAttendeeUnknownException(string address)
        : base("BOOKING_ATTENDEE_UNKNOWN", StatusCodes.Status400BadRequest,
            $"'{address}' is not a person this archive can book: either no user holds that address, or no "
            + "bookable resource represents them. The booking was refused rather than made without them.")
    {
    }
}
