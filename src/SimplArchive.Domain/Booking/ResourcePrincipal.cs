using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Booking;

/// <summary>
/// This resource document <b>represents</b> that person (ADR 0775) — a pilot dossier is Anna's, a desk is
/// Tom's. The mapping the booking primitive needs to answer two questions it otherwise cannot ask.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResourceBooking.ResourceDocumentId"/> points at a document, and <c>Document</c> has no link
/// saying whose person it is: <c>CreatedByUserId</c> is who filed it, <c>PersonalOfUserId</c> is a personal
/// space, and <see cref="ResourceBooking.BookedByUserId"/> is the BOOKER — not the claimant, so a student
/// booked by their instructor would not match. Without this table the core cannot answer "is the caller a
/// claimant on this booking?" or "which resources are mine?", which are the two halves of a person's
/// calendar.
/// </para>
/// <para>
/// A separate table rather than a column on <c>Documents</c>: the fact is said ONCE here instead of being
/// repeated on every claim, and the hottest table in the schema gains no column that is null for nearly
/// every row — the same reasoning that made a document reference an edge rather than a column (ADR 0773).
/// </para>
/// <para>
/// <b>Nothing in the core writes it.</b> A module declares that one of its documents stands for a person;
/// the core only reads. So this is inert until that seam exists, which is deliberate — the primitive
/// arriving before its writer is what the next slice stands on.
/// </para>
/// </remarks>
public class ResourcePrincipal : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The document that stands for a person — a bookable resource whose time is theirs.</summary>
    public Guid ResourceDocumentId { get; set; }

    /// <summary>Who it represents.</summary>
    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
