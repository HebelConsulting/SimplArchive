using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>
/// A module that vets bookings could not be consulted, so the booking was not accepted (ADR 0781).
/// </summary>
/// <remarks>
/// Its own code, distinct from both a slot conflict and a module's own refusal, because it tells the reader
/// something neither of those does: nothing is wrong with this booking — it was never judged. A client
/// should say so rather than advise picking another time, and an administrator should be looking at the
/// named module rather than at the schedule.
///
/// <b>503 rather than 409.</b> The others are the world saying no to a well-formed request; this is the
/// service being unable to answer, which is temporary, not the caller's doing, and worth retrying once the
/// module is fixed.
/// </remarks>
public sealed class BookingVettingUnavailableException : BookingException
{
    public BookingVettingUnavailableException(string detail)
        : base("BOOKING_VETTING_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable, detail)
    {
    }
}
