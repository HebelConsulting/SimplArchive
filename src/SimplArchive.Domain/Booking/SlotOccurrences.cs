using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace SimplArchive.Domain.Booking;

/// <summary>One occurrence of a repeating slot: a real span of time.</summary>
public readonly record struct SlotOccurrence(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc)
{
    /// <summary>Whether this occurrence covers <paramref name="from"/>–<paramref name="to"/> ENTIRELY.</summary>
    public bool Covers(DateTimeOffset from, DateTimeOffset to) => StartsAtUtc <= from && to <= EndsAtUtc;

    /// <summary>Whether this occurrence overlaps the range, half-open: touching spans do not.</summary>
    public bool Overlaps(DateTimeOffset from, DateTimeOffset to) => StartsAtUtc < to && from < EndsAtUtc;
}

/// <summary>
/// Expands a slot's recurrence into the occurrences that fall in a range.
/// </summary>
/// <remarks>
/// <para>
/// The expansion is ON DEMAND rather than materialised into rows, which is what keeps a row 1:1 with its
/// <c>.ics</c> document — the identity the whole booking primitive rests on (ADR 0744) and the shape of its
/// unique index. It also means an endless rule needs no horizon: you only ever expand across the range being
/// asked about, so "every Monday, forever" is answerable without deciding how far forever goes.
/// </para>
/// <para>
/// Built on Ical.Net rather than hand-rolled: RRULE is the format's own arithmetic — BYDAY, BYSETPOS, leap
/// years, month-end clamping — and a partial reimplementation would be wrong in ways only a real calendar
/// would reveal, on a rule somebody depends on.
/// </para>
/// </remarks>
public static class SlotOccurrences
{
    /// <summary>
    /// The occurrences of <paramref name="slot"/> that touch <paramref name="from"/>–<paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// A non-repeating slot yields itself when it touches the range, which is what lets every caller treat the
    /// two cases alike instead of branching on "does this repeat" at each site.
    /// </remarks>
    public static IReadOnlyList<SlotOccurrence> Between(IRecurringSlot slot, DateTimeOffset from, DateTimeOffset to)
    {
        var first = new SlotOccurrence(slot.StartsAtUtc.ToUniversalTime(), slot.EndsAtUtc.ToUniversalTime());

        if (string.IsNullOrWhiteSpace(slot.RecurrenceRule))
        {
            return first.Overlaps(from, to) ? [first] : [];
        }

        var duration = first.EndsAtUtc - first.StartsAtUtc;
        var starts = ExpandStarts(slot, first.StartsAtUtc, from - duration, to);

        return [.. starts
            .Select(start => new SlotOccurrence(start, start + duration))
            .Where(occurrence => occurrence.Overlaps(from, to))
            .OrderBy(occurrence => occurrence.StartsAtUtc)];
    }

    /// <summary>Whether ANY single occurrence covers the whole of <paramref name="from"/>–<paramref name="to"/>.</summary>
    /// <remarks>
    /// Covers, not overlaps, and by a SINGLE occurrence — the rule ADR 0780 already states for one-off
    /// windows, unchanged by repetition: a window ending at 12:00 does not consent to a booking running until
    /// 13:00 merely because the two touch, and two adjacent occurrences are not one offer.
    /// </remarks>
    public static bool AnyCovers(IRecurringSlot slot, DateTimeOffset from, DateTimeOffset to) =>
        Between(slot, from, to).Any(occurrence => occurrence.Covers(from, to));

    /// <summary>Whether the rule ends — carries <c>UNTIL</c> or <c>COUNT</c>, or does not repeat at all.</summary>
    /// <remarks>
    /// A claim and a block must be bounded: two endless claims cannot be compared for overlap in the general
    /// case, so allowing them would mean detection that is either wrong or arbitrarily capped — and an endless
    /// claim on a shared resource is a commitment nobody can outlive. An OFFER may be endless, because it
    /// takes nothing from anyone.
    /// </remarks>
    public static bool IsBounded(IRecurringSlot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.RecurrenceRule))
        {
            return true;
        }

        var pattern = new RecurrencePattern(slot.RecurrenceRule);
        return pattern.Until is not null || pattern.Count is > 0;
    }

    private static IEnumerable<DateTimeOffset> ExpandStarts(
        IRecurringSlot slot, DateTimeOffset firstStart, DateTimeOffset searchFrom, DateTimeOffset searchTo)
    {
        // A CalendarEvent assembled in memory rather than the stored bytes re-parsed: the row already holds
        // everything expansion needs, and reading the .ics back would put an object-storage fetch inside an
        // invariant that runs on every booking write.
        // DtEnd rather than a Duration: Ical.Net refuses both at once, and the end is what the row already
        // holds. Only the STARTS are taken from the expansion — each occurrence keeps the first one's length.
        var calendarEvent = new CalendarEvent
        {
            DtStart = ToCalDateTime(firstStart),
            DtEnd = ToCalDateTime(slot.EndsAtUtc.ToUniversalTime()),
            RecurrenceRule = new RecurrencePattern(slot.RecurrenceRule!),
        };

        foreach (var cancelled in ParseExceptionDates(slot.ExceptionDates))
        {
            calendarEvent.ExceptionDates.Add(ToCalDateTime(cancelled));
        }

        var calendar = new Calendar();
        calendar.Events.Add(calendarEvent);

        // Searched from one duration BEFORE the range, so an occurrence that starts earlier and runs into it
        // is found — the case that decides whether a night-spanning window covers an early-morning booking.
        return calendar.GetOccurrences(ToCalDateTime(searchFrom))
            .TakeWhile(occurrence => occurrence.Period.StartTime.AsUtc <= searchTo.UtcDateTime)
            .Select(occurrence => new DateTimeOffset(occurrence.Period.StartTime.AsUtc, TimeSpan.Zero));
    }

    private static CalDateTime ToCalDateTime(DateTimeOffset value) =>
        new(DateOnly.FromDateTime(value.UtcDateTime), TimeOnly.FromDateTime(value.UtcDateTime), "UTC");

    /// <summary>The cancelled instants, tolerant of a value written by something other than this server.</summary>
    private static IEnumerable<DateTimeOffset> ParseExceptionDates(string? exceptionDates) =>
        string.IsNullOrWhiteSpace(exceptionDates)
            ? []
            : exceptionDates
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => DateTimeOffset.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? parsed
                    : (DateTimeOffset?)null)
                .Where(parsed => parsed is not null)
                .Select(parsed => parsed!.Value);
}
