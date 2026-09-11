namespace SimplArchive.Domain.Booking;

/// <summary>
/// A <see cref="ResourceAvailability"/> window's lifecycle (ADR 0780).
/// </summary>
/// <remarks>
/// Two values rather than a deletion, matching <see cref="BookingStatus"/> and <see cref="BlockStatus"/>:
/// "was this instructor offering Thursday afternoon when the flight was booked?" is a question somebody will
/// ask after the fact, and a row deleted when the window is withdrawn cannot answer it.
/// </remarks>
public enum AvailabilityStatus
{
    /// <summary>Offered — the resource may be booked in this window.</summary>
    Offered,

    /// <summary>Withdrawn. Kept as history; offers nothing.</summary>
    Withdrawn,
}
