using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Booking;

/// <summary>
/// A window in which a bookable resource is OFFERED (ADR 0780) — the third fact about a resource's
/// timeline, beside the claims on it (<see cref="ResourceBooking"/>) and the times it is withdrawn
/// (<see cref="ResourceBlock"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A window is not a claim, and that distinction is the reason this table exists.</b> A booking made
/// inside an offered window MUST be able to overlap it — that overlap is precisely the answer to "is this
/// hour spoken for?" (#1093's derived label). Two claims on one resource may never overlap, so modelling
/// availability as a booking would have meant the first flight anyone booked was refused by the instructor's
/// own published availability.
/// </para>
/// <para>
/// Windows may also overlap <b>each other</b>, like blocks and unlike bookings: publishing 09:00–17:00 and
/// then 14:00–20:00 is somebody offering more time, not a conflict to refuse.
/// </para>
/// <para>
/// What a window MEANS is deliberately not decided here. The core stores offered time; whether a booking
/// requires one — an instructor's published availability standing as their consent, so a student may book
/// them without asking — is the owning module's rule, since it is the module that knows a student from an
/// instructor.
/// </para>
/// </remarks>
public class ResourceAvailability : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The resource offering the time (its mask has <c>IsBookable</c>).</summary>
    public Guid ResourceDocumentId { get; set; }

    /// <summary>
    /// The window document — the <c>.ics</c> in the resource's Availability collection. A plain column, not
    /// a FK, following <see cref="ResourceBooking.BookingDocumentId"/> and the
    /// <c>Document.CurrentVersionId</c> precedent (ADR 0503).
    /// </summary>
    public Guid WindowDocumentId { get; set; }

    /// <summary>Window start, inclusive. UTC instants, so the overlap test compares real moments.</summary>
    public DateTimeOffset StartsAtUtc { get; set; }

    /// <summary>Window end, exclusive — half-open, the same semantics the other two tables use.</summary>
    public DateTimeOffset EndsAtUtc { get; set; }

    /// <summary>Offered, or withdrawn and kept as history (ADR 0780).</summary>
    public AvailabilityStatus Status { get; set; }

    // Exactly one of OfferedByUserId/OfferedByServiceAccountId is set.
    public Guid? OfferedByUserId { get; set; }

    public Guid? OfferedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
