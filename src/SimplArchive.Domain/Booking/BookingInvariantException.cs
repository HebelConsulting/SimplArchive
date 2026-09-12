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
    private BookingInvariantException(BookingInvariantKind kind, string message, IReadOnlyList<SlotOccurrence>? offered = null)
        : base(message)
    {
        Kind = kind;
        Offered = offered ?? [];
    }

    /// <summary>
    /// The windows this resource DOES offer around the requested slot (#1135).
    /// </summary>
    /// <remarks>
    /// Structured, not prose. The message is English by construction — an exception's literal — and a client
    /// must never put that in front of a user (issue #424), so the values a refusal computed travel as DATA
    /// and each client composes "only available 08:00–22:00" in the reader's own language.
    /// <para>
    /// Empty for every other kind, and for a resource that offers nothing that day — which is itself the
    /// answer, and a different sentence from "offered, but not then".
    /// </para>
    /// </remarks>
    public IReadOnlyList<SlotOccurrence> Offered { get; }

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

    /// <summary>The resource is out of service for part or all of the requested slot (ADR 0778).</summary>
    /// <remarks>
    /// Distinct from <see cref="SlotTaken"/> because the two are different facts with different remedies: a
    /// taken slot means somebody else got there first and another time will do, while a blocked one means
    /// the aircraft is not airworthy and no time inside the block will do. Reporting a block as a conflict
    /// would send the caller hunting for a free hour that does not exist.
    ///
    /// Names the block's window for the same reason SlotTaken names the booking's — a refusal the caller can
    /// act on beats a bare no.
    /// </remarks>
    public static BookingInvariantException ResourceBlocked(
        DateTimeOffset requestedStart, DateTimeOffset requestedEnd, DateTimeOffset blockStart, DateTimeOffset blockEnd) =>
        new(BookingInvariantKind.ResourceBlocked, $"The requested slot {requestedStart:u}–{requestedEnd:u} falls in a maintenance "
            + $"block {blockStart:u}–{blockEnd:u}: the resource is out of service (ADR 0778).");

    /// <summary>A claim or a block repeats endlessly — it must carry <c>UNTIL</c> or <c>COUNT</c> (#1133).</summary>
    /// <remarks>
    /// An OFFER may repeat forever, because it takes nothing from anyone. A claim and a block may not: two
    /// endless claims cannot be compared for overlap in the general case, so allowing them would mean
    /// detection that is either wrong or arbitrarily capped — and an endless claim on a shared resource is a
    /// commitment nobody can outlive.
    /// </remarks>
    public static BookingInvariantException EndlessRecurrence(string rule) =>
        new(BookingInvariantKind.EndlessRecurrence, $"The repeat rule '{rule}' never ends. A booking or a maintenance "
            + "block must say when it stops — add UNTIL or COUNT. (An availability window may repeat endlessly.)");
    /// <summary>
    /// The slot is not covered by any window the resource has offered, and the resource HAS offered some.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than <see cref="ResourceBlocked"/>: a block says the resource is out of service and
    /// nothing but time fixes it, while this says the resource is simply not on offer then — a different
    /// sentence, and a different remedy (book inside a window, or publish one).
    /// <para>
    /// Only for a resource that has published at least one window. Publishing nothing keeps the old behaviour
    /// — book freely — so the rule cannot surprise a resource that never used availability, and a resource
    /// that HAS published means it.
    /// </para>
    /// </remarks>
    public static BookingInvariantException NotOffered(
        DateTimeOffset requestedStart, DateTimeOffset requestedEnd, IReadOnlyList<SlotOccurrence>? offered = null) =>
        new(BookingInvariantKind.NotOffered, $"The requested slot {requestedStart:u}–{requestedEnd:u} is not covered by any "
            + "window this resource has offered. A window must cover the WHOLE slot: one ending before the slot does "
            + "consents to part of it, not all of it.", offered);

    /// <summary>A block's window has no extent: start must precede end.</summary>
    /// <remarks>
    /// Its own kind rather than reusing <see cref="SlotWithoutExtent"/>: the message has to say which of the
    /// two a caller got wrong, and a block and a booking are written through different surfaces.
    /// </remarks>
    public static BookingInvariantException BlockWithoutExtent(DateTimeOffset start, DateTimeOffset end) =>
        new(BookingInvariantKind.BlockWithoutExtent, $"A block's window must have extent: start {start:u} does not precede end {end:u}.");

    /// <summary>An availability window's range has no extent: start must precede end.</summary>
    /// <remarks>Its own kind rather than reusing the booking's or the block's: the message has to say which
    /// of the three a caller got wrong, and each is written through a different surface.</remarks>
    public static BookingInvariantException WindowWithoutExtent(DateTimeOffset start, DateTimeOffset end) =>
        new(BookingInvariantKind.WindowWithoutExtent, $"An availability window must have extent: start {start:u} does not precede end {end:u}.");

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
    ResourceBlocked,
    EndlessRecurrence,
    NotOffered,
    BlockWithoutExtent,
    WindowWithoutExtent,
}
