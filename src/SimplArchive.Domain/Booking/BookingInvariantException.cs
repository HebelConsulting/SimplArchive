namespace SimplArchive.Domain.Booking;

/// <summary>
/// A <see cref="ResourceBooking"/> broke one of the booking primitive's invariants (ADR 0735).
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so the DbContext's invariants surface as the type
/// the Api boundary has always translated — the <see cref="Masks.TypedFolderContainmentException"/>
/// precedent, and for the same reason: a dedicated type lets the boundary tell this refusal apart from the
/// other invariants that share the base, so a slot conflict is never reported as a name collision.
/// </remarks>
public sealed class BookingInvariantException : InvalidOperationException
{
    private BookingInvariantException(BookingInvariantKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Which invariant refused — so a boundary translates by FACT, not by matching message text
    /// (a message-substring dispatch is a carve-out that verifies prose; ADR 0744 added a second caller
    /// and made the fragility load-bearing).</summary>
    public BookingInvariantKind Kind { get; }

    /// <summary>The slot is taken: an Active booking of the same resource overlaps the requested range.</summary>
    /// <remarks>
    /// Names the occupied range rather than just refusing — a refusal the caller can act on (when IS it
    /// free?) beats a bare "conflict", per the walkthrough's lesson that a rejection without its reason
    /// reads as a broken button. The stand-by queue (ADR 0735, later slice) will turn this refusal into a
    /// queued claim; until then it is final.
    /// </remarks>
    public static BookingInvariantException SlotTaken(
        DateTimeOffset requestedStart, DateTimeOffset requestedEnd, DateTimeOffset takenStart, DateTimeOffset takenEnd) =>
        new(BookingInvariantKind.SlotTaken, $"The requested slot {requestedStart:u}–{requestedEnd:u} overlaps an existing booking "
            + $"{takenStart:u}–{takenEnd:u} of the same resource (ADR 0735; stand-by queuing is a later slice).");

    /// <summary>The target document's mask does not declare it bookable.</summary>
    public static BookingInvariantException NotBookable(Guid resourceDocumentId) =>
        new(BookingInvariantKind.NotBookable, $"Document {resourceDocumentId} is not a bookable resource — its mask does not declare "
            + "IsBookable (ADR 0735).");

    /// <summary>The slot has no extent: start must precede end.</summary>
    public static BookingInvariantException SlotWithoutExtent(DateTimeOffset start, DateTimeOffset end) =>
        new(BookingInvariantKind.SlotWithoutExtent, $"A booking's slot must have extent: start {start:u} does not precede end {end:u}.");

    /// <summary>Two claims of the same booking document disagree about when the booking is.</summary>
    /// <remarks>
    /// A booking may now claim SEVERAL resources at once (ADR 0774) — a training flight occupies the
    /// aircraft, the student and the instructor for one window — so the rule that a document carries one
    /// claim is gone. What replaces it is this: every claim of one document names the SAME window, because
    /// they are one event. Without it, three rows could drift apart and the document would describe a
    /// booking none of its claims agreed with.
    /// </remarks>
    public static BookingInvariantException ClaimsDisagreeOnSlot(
        Guid bookingDocumentId, DateTimeOffset start, DateTimeOffset end, DateTimeOffset otherStart, DateTimeOffset otherEnd) =>
        new(BookingInvariantKind.ClaimsDisagreeOnSlot,
            $"The claims of booking document {bookingDocumentId} disagree about its slot: {start:u}–{end:u} "
            + $"against {otherStart:u}–{otherEnd:u}. Every claim of one booking names the same window (ADR 0774).");
}

/// <summary>The booking invariants a save can refuse on (one per factory above).</summary>
public enum BookingInvariantKind
{
    SlotTaken,
    NotBookable,
    SlotWithoutExtent,
    ClaimsDisagreeOnSlot,
}
