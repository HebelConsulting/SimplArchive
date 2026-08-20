namespace SimplArchive.Presentation;

/// <summary>
/// Which days an appointment occupies in a calendar grid — the one answer both clients must give identically.
/// </summary>
/// <remarks>
/// <para>
/// A month grid places a chip per day. Placing it only on the entry's START day is the obvious implementation
/// and it is wrong: a three-day conference then appears on day one and vanishes, so the grid reads as "nothing
/// is happening" on exactly the days something is. The sister project shipped that and had to correct it
/// afterwards; this is the correction, arrived at first.
/// </para>
/// <para>
/// <b>The end is EXCLUSIVE at midnight, in both shapes.</b> iCalendar's <c>DTEND</c> for an all-day entry names
/// the day it STOPS — a two-day festival on the 15th has <c>DTEND</c> of the 17th — and a timed entry finishing
/// at exactly 00:00 stopped the previous evening. Neither adds a day. That is a single off-by-one in each
/// direction, invisible until somebody counts, which is why it is written once and tested once here rather
/// than twice in two clients that would drift.
/// </para>
/// <para>
/// Kept free of any row type: the two clients model a row as a record and as an observable view-model
/// respectively, and a shared type that knew about either would be the wrong shape for the other.
/// </para>
/// </remarks>
public static class AppointmentCoverage
{
    /// <summary>What a cell shows in place of a time on a day the entry merely runs through.</summary>
    /// <remarks>
    /// A horizontal ellipsis rather than an arrow: it carries no direction, which is right for a cell that is
    /// neither the start nor the end of the run. Not localized — it is a mark, not a word.
    /// </remarks>
    public const string ContinuationMark = "…";

    /// <summary>
    /// The marker on an entry that repeats — the one thing standing between the grid and a quiet lie.
    /// </summary>
    /// <remarks>
    /// A recurrence set is never expanded (the epic's decision), so a weekly rehearsal is drawn at its FIRST
    /// occurrence and nowhere else. That limitation is defensible; a SILENT one is not, because a month showing
    /// the entry once is indistinguishable from a month in which it happens once. The mark says "there is more
    /// here than is drawn"; its meaning rides in the tooltip, so this stays a mark rather than a word and needs
    /// no translation.
    /// </remarks>
    public const string RepeatMark = "↻";

    /// <summary>
    /// The last day the entry actually covers, or <c>null</c> when it is undated.
    /// </summary>
    /// <param name="firstDay">The day it starts on — indexed for an all-day entry, else the start's local date.</param>
    /// <param name="allDay">Whether this is a day rather than a moment.</param>
    /// <param name="endDay">The indexed <c>DTEND</c> DAY, for the all-day shape. Exclusive.</param>
    /// <param name="ends">The end instant, for the timed shape.</param>
    /// <remarks>
    /// A missing, zero or negative duration covers the start day ALONE rather than nothing: bad data should
    /// draw the entry once, never make it disappear, because an entry nobody can see is indistinguishable from
    /// one that was never filed.
    /// </remarks>
    public static DateOnly? LastDay(DateOnly? firstDay, bool allDay, DateOnly? endDay, DateTimeOffset? ends)
    {
        if (firstDay is not { } first)
        {
            return null;
        }

        var last = allDay
            ? endDay is { } stops && stops > first ? stops.AddDays(-1) : first
            : ends is { } moment ? LastCoveredDay(moment, first) : first;

        return last < first ? first : last;
    }

    /// <summary>Whether the entry is on show on <paramref name="day"/> — the grid's bucketing question.</summary>
    /// <remarks>An undated entry covers no day at all; it belongs to the list, which sorts it last.</remarks>
    public static bool CoversDay(
        DateOnly day, DateOnly? firstDay, bool allDay, DateOnly? endDay, DateTimeOffset? ends) =>
        firstDay is { } first
        && LastDay(first, allDay, endDay, ends) is { } last
        && day >= first
        && day <= last;

    /// <summary>
    /// True on a covered day that is not the first — the cell reads as "still going" rather than "starts here".
    /// </summary>
    /// <remarks>
    /// Repeating the start time on day three would state the entry began that morning, which is the one thing
    /// a continuation cell must not say.
    /// </remarks>
    public static bool ContinuesOn(
        DateOnly day, DateOnly? firstDay, bool allDay, DateOnly? endDay, DateTimeOffset? ends) =>
        CoversDay(day, firstDay, allDay, endDay, ends) && day != firstDay;

    private static DateOnly LastCoveredDay(DateTimeOffset ends, DateOnly first)
    {
        var local = ends.LocalDateTime;
        var day = DateOnly.FromDateTime(local);
        return local.TimeOfDay == TimeSpan.Zero && day > first ? day.AddDays(-1) : day;
    }
}
