namespace SimplArchive.Presentation;

/// <summary>
/// One appointment's start and end read in one time zone, with the zone each endpoint was recorded in.
/// </summary>
/// <param name="StartZoneId">
/// The IANA id the start is written in, or <see langword="null"/> for a FLOATING time — which is a real
/// distinction and not a missing value: a floating entry happens at that wall-clock time wherever the reader
/// is, so the client says so in words rather than naming a zone the file does not contain.
/// </param>
/// <param name="EndZoneId">The same for the end, which iCalendar allows to differ from the start's.</param>
public sealed record ZonedRange(DateTimeOffset? Start, DateTimeOffset? End, string? StartZoneId, string? EndZoneId)
{
    /// <summary>
    /// Whether both endpoints are in the same zone — so the pane names it once instead of per endpoint.
    /// </summary>
    public bool OneZone => End is null || StartZoneId == EndZoneId;

    /// <summary>
    /// The range as one line: <c>24/08/2026 09:00 – 11:30</c>, repeating the date only when the end falls on
    /// another day, and the start alone when there is no end.
    /// </summary>
    /// <remarks>
    /// Here rather than in each client because collapsing a same-day range to one date is a DECISION about what
    /// the reader is told, not a style — and two copies of it is how the same appointment comes to read two ways
    /// on two screens. What the string is drawn INTO stays each client's business (ADR 0651).
    /// </remarks>
    public string Format(IFormatProvider culture) => (Start, End) switch
    {
        (null, _) => string.Empty,
        ({ } s, null) => $"{s.DateTime.ToString("g", culture)}",
        ({ } s, { } e) when s.Date == e.Date => $"{s.DateTime.ToString("g", culture)} – {e.DateTime.ToString("t", culture)}",
        ({ } s, { } e) => $"{s.DateTime.ToString("g", culture)} – {e.DateTime.ToString("g", culture)}",
    };

    /// <summary>The range in ONE endpoint's terms, for a pane that names the two zones on separate lines.</summary>
    public string FormatStart(IFormatProvider culture) =>
        Start is { } s ? s.DateTime.ToString("g", culture) : string.Empty;

    /// <inheritdoc cref="FormatStart"/>
    public string FormatEnd(IFormatProvider culture) =>
        End is { } e ? e.DateTime.ToString("g", culture) : string.Empty;
}

/// <summary>
/// The three readings of an appointment's time that a detail pane shows, and whether the third is worth
/// showing at all.
/// </summary>
/// <remarks>
/// <para>
/// A calendar entry is one instant described three ways, and a reader needs different ones at different
/// moments: <b>UTC</b> to compare it with anything else, <b>as recorded</b> to see what the organiser wrote,
/// and <b>in their own zone</b> to know when to be somewhere. Showing only the last is the usual choice and it
/// loses the first two — which matters most for exactly the entries where it matters at all: a flight, a call
/// across continents, anything an organiser wrote in a zone that is not the reader's.
/// </para>
/// <para>
/// <b>The end carries its own zone.</b> iCalendar allows <c>DTSTART</c> and <c>DTEND</c> to name different
/// TZIDs, and a flight leaving Zurich at 09:00 and landing in Boston at 11:30 is exactly that. Collapsing the
/// two into one zone — which every earlier version of this code did — makes the same flight read as two and a
/// half hours, in the wrong place.
/// </para>
/// <para>
/// <b>Shared because the two clients must not answer it differently.</b> This is arithmetic, not formatting:
/// which instants, in which zones, and whether the viewer's own reading is redundant. How a time is drawn into
/// a row stays each client's business (ADR 0651).
/// </para>
/// </remarks>
public sealed record AppointmentTimes(ZonedRange Utc, ZonedRange Recorded, ZonedRange? Viewer)
{
    /// <summary>
    /// Reads a timed appointment's three blocks, or <see langword="null"/> when there is no instant to place.
    /// </summary>
    /// <param name="start">The start as WRITTEN in the file — a wall clock in <paramref name="startZoneId"/>.</param>
    /// <param name="end">The end as written, in <paramref name="endZoneId"/>. Null when the entry has none.</param>
    /// <param name="isAllDay">An all-day entry has no time to place, and none may be invented (ADR 0647).</param>
    /// <param name="viewerZone">The reader's own zone — passed in rather than read here, so it is testable.</param>
    /// <returns>
    /// Null for an all-day entry and for one with no start: a day is not a moment, and stamping midnight on it
    /// would invent one. The caller shows its single all-day row instead.
    /// </returns>
    public static AppointmentTimes? For(
        DateTime? start, DateTime? end, bool isAllDay, string? startZoneId, string? endZoneId, TimeZoneInfo viewerZone)
    {
        if (isAllDay || start is not { } startWall)
        {
            return null;
        }

        var startZone = Resolve(startZoneId, viewerZone);
        var endZone = Resolve(endZoneId, viewerZone);

        var recordedStart = At(startWall, startZone);
        var recordedEnd = end is { } endWall ? At(endWall, endZone) : (DateTimeOffset?)null;

        var utc = new ZonedRange(
            recordedStart.ToUniversalTime(), recordedEnd?.ToUniversalTime(), "UTC", "UTC");
        var recorded = new ZonedRange(recordedStart, recordedEnd, startZoneId, endZoneId);

        var viewer = new ZonedRange(
            TimeZoneInfo.ConvertTime(recordedStart, viewerZone),
            recordedEnd is { } e ? TimeZoneInfo.ConvertTime(e, viewerZone) : null,
            viewerZone.Id,
            viewerZone.Id);

        // The viewer's own reading is dropped when it would repeat the recorded one — the case where the
        // organiser wrote the entry in the reader's own zone, which is most of them.
        //
        // Compared by OFFSET at those instants rather than by zone id, because the ids are not comparable
        // across platforms: a TZID out of an .ics is IANA ("Europe/Zurich") while TimeZoneInfo.Local.Id is a
        // Windows id on Windows, so an id comparison would show a redundant third block to every Windows user
        // and no one else. Offsets answer the question the block is for — would it show the same numbers — and
        // they answer it identically everywhere.
        var sameStart = recordedStart.Offset == viewer.Start!.Value.Offset;
        var sameEnd = recordedEnd is null || recordedEnd.Value.Offset == viewer.End!.Value.Offset;

        return new AppointmentTimes(utc, recorded, sameStart && sameEnd ? null : viewer);
    }

    /// <summary>The wall clock placed in its zone — the same construction the server's index stamp uses.</summary>
    private static DateTimeOffset At(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>
    /// The named zone, falling back to the reader's own for a floating time and for an id this host does not
    /// carry.
    /// </summary>
    /// <remarks>
    /// A floating time genuinely means "whatever zone you are in", so the fallback IS the rule there. For an
    /// unknown TZID it is a compromise the server already makes when indexing: the alternative is refusing to
    /// place the entry at all, which would leave a whole calendar unreadable because of one exotic zone. The
    /// RECORDED block still shows the id as written, so the reader is never told the entry names a zone it
    /// does not.
    /// </remarks>
    private static TimeZoneInfo Resolve(string? zoneId, TimeZoneInfo viewerZone)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return viewerZone;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception)
        {
            return viewerZone;
        }
    }
}
