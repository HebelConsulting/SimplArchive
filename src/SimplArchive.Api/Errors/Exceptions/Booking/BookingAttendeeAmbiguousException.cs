using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// An <c>ATTENDEE</c> names somebody who has more than one bookable resource, so there is no way to tell
/// which of their calendars the booking should claim (ADR 0776).
/// </summary>
/// <remarks>
/// Legal state, not a corruption: the mapping is unique on the RESOURCE, not on the user, precisely because
/// one person holding two resources is odd but harmless. It only becomes a question here, where a claim has
/// to name exactly one of them — and it is refused rather than guessed, because booking the wrong one of
/// somebody's two calendars is a mistake nobody would think to look for.
/// </remarks>
public sealed class BookingAttendeeAmbiguousException : BookingException
{
    public BookingAttendeeAmbiguousException(string address, int resourceCount)
        : base("BOOKING_ATTENDEE_AMBIGUOUS", StatusCodes.Status400BadRequest,
            $"'{address}' is represented by {resourceCount} bookable resources, so this booking cannot tell "
            + "which one to claim. Name the resource directly, or leave the person with a single one.")
    {
    }
}
