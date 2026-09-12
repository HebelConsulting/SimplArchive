namespace SimplArchive.Domain.Booking;

/// <summary>
/// A span of time that may repeat — an availability window, a booking claim or a maintenance block.
/// </summary>
/// <remarks>
/// <para>
/// One interface for the three because the question asked of them is identical — <i>which occurrences of this
/// fall between these two instants?</i> — and only the meaning of an occurrence differs: offered, claimed, out
/// of service. Three copies of that expansion is how one of them comes to disagree with the others about
/// whether an EXDATE counts, or whether an occurrence that starts before the range but ends inside it is in.
/// </para>
/// <para>
/// <see cref="StartsAtUtc"/> and <see cref="EndsAtUtc"/> stay the FIRST occurrence — the <c>DTSTART</c> and
/// <c>DTEND</c> of the stored <c>.ics</c>. Every row written before recurrence existed therefore reads as a
/// single occurrence with no rule, which is what makes the change purely additive.
/// </para>
/// </remarks>
public interface IRecurringSlot
{
    /// <summary>The first occurrence's start.</summary>
    DateTimeOffset StartsAtUtc { get; }

    /// <summary>The first occurrence's end, exclusive.</summary>
    DateTimeOffset EndsAtUtc { get; }

    /// <summary>
    /// The <c>RRULE</c> value (<c>FREQ=WEEKLY;COUNT=12</c>), or null for a slot that happens once.
    /// </summary>
    string? RecurrenceRule { get; }

    /// <summary>
    /// The cancelled occurrences — the <c>EXDATE</c> instants, ISO-8601, comma-separated; null for none.
    /// </summary>
    /// <remarks>
    /// Cancelling ONE occurrence of a series is what iCalendar already says it is, because the booking IS the
    /// <c>.ics</c> (ADR 0744): any CalDAV client writes an EXDATE natively. Honouring it is what keeps the
    /// stored claim and the one the client displays from disagreeing about a single day.
    /// </remarks>
    string? ExceptionDates { get; }
}
