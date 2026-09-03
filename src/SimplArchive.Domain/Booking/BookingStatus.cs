namespace SimplArchive.Domain.Booking;

/// <summary>
/// A <see cref="ResourceBooking"/>'s lifecycle as the CORE sees it (ADR 0735): booked or not. The rich
/// lifecycle — accepted, out, returned — is the owning MODULE's state machine (ADR 0742), derived from
/// documents, never stored here.
/// </summary>
public enum BookingStatus
{
    /// <summary>Holds its slot; participates in the no-overlap invariant.</summary>
    Active,

    /// <summary>Released its slot; kept as history rather than deleted.</summary>
    Cancelled,
}
