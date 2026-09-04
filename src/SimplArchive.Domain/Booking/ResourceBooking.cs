using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Booking;

/// <summary>
/// One claim on a bookable resource for a time slot — the core inventory-booking primitive's own record
/// (ADR 0735). The resource and the booking are both DOCUMENTS (a meeting room, a charter); this row is
/// what ties them to an authoritative slot, because the no-overlap invariant needs indexed instants and
/// EAV field values cannot give a portable, efficient overlap check.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartsAtUtc"/>/<see cref="EndsAtUtc"/> are the AUTHORITATIVE slot (ADR 0743: the booking
/// owns; the linked appointment document's <c>Start</c>/<c>End</c> index fields are a projection kept in
/// lockstep — the Repository-mask lockstep precedent, ADR 0627).
/// </para>
/// <para>
/// Deliberately absent in slice 1 (ADR 0735's scope): the stand-by queue and entitlement tiers — both
/// land later behind this same seam. A booking against a taken slot is simply refused for now.
/// </para>
/// </remarks>
public class ResourceBooking : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The bookable resource document (its mask has <c>IsBookable</c>).</summary>
    public Guid ResourceDocumentId { get; set; }

    /// <summary>
    /// The booking document — the <c>.ics</c> in the resource's Schedule (ADR 0744: the booking IS the
    /// calendar entry). A plain column, not a FK — the <c>Document.CurrentVersionId</c> precedent
    /// (ADR 0503): a FK would entangle the document's delete cascade with the booking's lifecycle, and a
    /// Cancelled row deliberately outlives a purged document as the durable history.
    /// </summary>
    public Guid BookingDocumentId { get; set; }

    /// <summary>Slot start, inclusive. UTC instants, so the overlap check compares real moments.</summary>
    public DateTimeOffset StartsAtUtc { get; set; }

    /// <summary>Slot end, exclusive — back-to-back bookings (10:00–11:00, 11:00–12:00) do not overlap.</summary>
    public DateTimeOffset EndsAtUtc { get; set; }

    public BookingStatus Status { get; set; }

    // Exactly one of BookedByUserId/BookedByServiceAccountId is set (the DocumentReference precedent).
    public Guid? BookedByUserId { get; set; }

    public Guid? BookedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
