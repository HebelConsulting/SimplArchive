namespace SimplArchive.Api.Documents;

/// <summary>
/// Turning a calendar time into a comparable instant — shared by the classifier (which indexes it) and
/// <see cref="ResourceCollectionWriter"/> (which stores it on a claim, a block or a window).
/// </summary>
/// <remarks>
/// One copy rather than two, because the two would answer differently the first time either was touched:
/// an all-day value becomes midnight in the SERVER's zone, and a claim row disagreeing with the indexed
/// Start about which day a booking is on is precisely the drift ADR 0743's lockstep exists to prevent.
/// </remarks>
internal static class CalendarInstants
{
    /// <summary>The instant a calendar time names, or null when it names none.</summary>
    /// <remarks>An all-day value becomes midnight in the SERVER's zone — one comparable instant beats an
    /// invented UTC midnight nobody's wall clock shows.</remarks>
    internal static DateTimeOffset? Instant(Ical.Net.DataTypes.CalDateTime? when)
    {
        if (when?.Value is not { } value)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        var offset = !when.HasTime || string.IsNullOrEmpty(when.TzId)
            ? TimeZoneInfo.Local.GetUtcOffset(local)
            : ZoneOffset(when.TzId, local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>The zone's offset at that local time, falling back to the server's when the id is unknown.</summary>
    /// <remarks>
    /// A TZID names an IANA zone the host may not carry (a Windows host without ICU, say). Falling back keeps
    /// the item indexed and findable rather than dropping its time entirely, which is the failure that would
    /// make a whole calendar unsortable because of one exotic zone.
    /// </remarks>
    internal static TimeSpan ZoneOffset(string timeZoneId, DateTime local)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(local);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Local.GetUtcOffset(local);
        }
    }
}
