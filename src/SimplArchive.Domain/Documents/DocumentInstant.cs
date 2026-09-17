namespace SimplArchive.Domain.Documents;

/// <summary>
/// The UTC instant a document's date+time denote — or <c>null</c> when it was dated to the DAY (#1254).
/// </summary>
/// <remarks>
/// <para>
/// <c>DocumentVersion.DocumentDate</c> and <c>DocumentTime</c> are ONE instant split across two columns, so
/// anything that converts one without the other produces a row disagreeing with itself: 00:30 in Zurich is
/// 22:30 on the PREVIOUS day in UTC.
/// </para>
/// <para>
/// <b>Null is a real answer and the reason this returns one.</b> A document dated to the day — most of them —
/// never WAS an instant: somebody chose a calendar date, and no zone conversion applies to it. Giving it a
/// notional midnight would make it move between zones, so a document filed "on the 15th" would start reading
/// as the 14th for anyone west of the server. Those documents match their date in every zone, which is both
/// correct and what a user expects.
/// </para>
/// <para>
/// Shared rather than duplicated because both clients and the search indexer must agree about which documents
/// convert — two copies of that rule is how a listing and a search come to disagree about the same document.
/// </para>
/// </remarks>
public static class DocumentInstant
{
    public static DateTimeOffset? Of(DateOnly date, TimeOnly? time) =>
        time is { } at ? new DateTimeOffset(date.ToDateTime(at), TimeSpan.Zero) : null;

    /// <summary>
    /// The half-open UTC range covering one CALENDAR DAY as seen from <paramref name="zone"/>.
    /// </summary>
    /// <remarks>
    /// Half-open on purpose: an inclusive upper bound either double-counts midnight or drops the last second,
    /// and both are the kind of error nobody notices until a document goes missing on exactly one day.
    /// <para>
    /// The offset is resolved AT each boundary rather than once for the day, because a DST transition inside
    /// that day makes the two ends differ — the whole reason this is a function and not an addition.
    /// </para>
    /// </remarks>
    public static (DateTimeOffset From, DateTimeOffset To) DayIn(DateOnly day, TimeZoneInfo zone)
    {
        var startLocal = day.ToDateTime(TimeOnly.MinValue);
        var endLocal = day.AddDays(1).ToDateTime(TimeOnly.MinValue);

        return (Utc(startLocal, zone), Utc(endLocal, zone));
    }

    // A local wall clock converted to UTC. Correct for both DST shapes WITHOUT a special case, which is worth
    // stating because the special case is the obvious thing to write and it is wrong twice over:
    //
    //   * a SKIPPED time (00:00 on 6 Sep in America/Santiago does not exist) — GetUtcOffset returns the
    //     pre-transition offset, and the instant that yields is already the first real one after the gap. A
    //     hand-rolled "add an hour" produces the SAME answer where the gap is an hour, so it is dead code —
    //     and a WRONG answer where it is not, as on Lord Howe Island, whose clocks move thirty minutes.
    //   * a REPEATED time (02:30 twice when clocks go back) — GetUtcOffset picks standard time, i.e. the
    //     second occurrence. Either is defensible for a day BOUNDARY, and picking one consistently is what
    //     keeps consecutive days meeting exactly rather than overlapping by an hour.
    //
    // Verified rather than assumed: the guard was written first, and removing it changed no result, which is
    // how it was found to be doing nothing.
    private static DateTimeOffset Utc(DateTime local, TimeZoneInfo zone) =>
        new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
}
