using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Booking;

/// <summary>
/// A window in which a bookable resource is out of service (ADR 0778) — maintenance, a defect, an
/// inspection. The counterpart of <see cref="ResourceBooking"/>: a booking CLAIMS a resource, a block
/// WITHDRAWS one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own table rather than a flag on <see cref="ResourceBooking"/>.</b> A block MAY overlap — two
/// defects reported on one aircraft are two blocks, and refusing the second would mean the second finding
/// cannot be recorded — which is the exact opposite of the booking invariant. A discriminator column would
/// therefore have to be excluded from the overlap check, and from every other booking query besides;
/// missing one silently either makes a block conflict-check like a booking, or makes bookings ignore each
/// other. A separate table cannot be forgotten.
/// </para>
/// <para>
/// <b>Suspension is DERIVED from this row, never stored</b> (owner decision, #1091): a booking is suspended
/// exactly while an Active block of its resource overlaps its slot. So there is no suspension state to
/// update, none to leave stale when a block is cleared, and none a pilot could edit to un-ground an
/// aircraft — the only way out is clearing the block, which needs <c>CanReleaseResources</c>.
/// </para>
/// </remarks>
public class ResourceBlock : ITenantScoped, IConcurrencyTracked, IRecurringSlot
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The blocked resource document (its mask has <c>IsBookable</c>) — the same resources a
    /// booking may claim, since a block is only meaningful against something claimable.</summary>
    public Guid ResourceDocumentId { get; set; }

    /// <summary>
    /// The block document — the <c>.ics</c> in the resource's Maintenance collection. A plain column, not a
    /// FK, following <see cref="ResourceBooking.BookingDocumentId"/> and the <c>Document.CurrentVersionId</c>
    /// precedent (ADR 0503): a Cleared row deliberately outlives a purged document as the durable record of
    /// when the resource was unavailable.
    /// </summary>
    public Guid BlockDocumentId { get; set; }

    /// <summary>Block start, inclusive. UTC instants, so the overlap test compares real moments.</summary>
    public DateTimeOffset StartsAtUtc { get; set; }

    /// <summary>Block end, exclusive — a block ending at 10:00 leaves a booking starting at 10:00 alone.</summary>
    public DateTimeOffset EndsAtUtc { get; set; }

    /// <summary>Active withdraws the resource; Cleared is history (ADR 0778).</summary>
    public BlockStatus Status { get; set; }

    // Exactly one of BlockedByUserId/BlockedByServiceAccountId is set (CK_ResourceBlocks_ExactlyOneCreator).
    public Guid? BlockedByUserId { get; set; }

    public Guid? BlockedByServiceAccountId { get; set; }

    /// <summary>The <c>RRULE</c> value when the block repeats. Must be BOUNDED, like a claim: it takes the resource out of service for others.</summary>
    /// <remarks>
    /// Stored rather than materialised into one row per occurrence (#1133), which is what keeps this row 1:1
    /// with its <c>.ics</c> document — the identity the booking primitive rests on (ADR 0744) and the shape of
    /// its unique index. <see cref="StartsAtUtc"/>/<see cref="EndsAtUtc"/> stay the FIRST occurrence, so every
    /// row written before recurrence existed reads as a single one.
    /// </remarks>
    public string? RecurrenceRule { get; set; }

    /// <summary>The cancelled occurrences — <c>EXDATE</c> instants, ISO-8601, comma-separated.</summary>
    public string? ExceptionDates { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
