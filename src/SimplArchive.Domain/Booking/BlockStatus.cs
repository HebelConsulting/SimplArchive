namespace SimplArchive.Domain.Booking;

/// <summary>
/// A <see cref="ResourceBlock"/>'s lifecycle (ADR 0778): withdrawing the resource, or done.
/// </summary>
/// <remarks>
/// Two values rather than a deletion, for the same reason <see cref="BookingStatus"/> keeps Cancelled: an
/// aircraft that was out of service for three days in March is a fact somebody will need to answer for, and
/// a row that is deleted when the block is lifted cannot answer it. Clearing is the act that ends a block's
/// effect; it is not the act of forgetting it happened.
/// </remarks>
public enum BlockStatus
{
    /// <summary>Withdraws the resource: overlapping bookings are suspended, new ones refused.</summary>
    Active,

    /// <summary>Released to service. Kept as history — everything it suspended is live again.</summary>
    Cleared,
}
