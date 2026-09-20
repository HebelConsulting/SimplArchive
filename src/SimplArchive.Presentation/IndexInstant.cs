using System.Globalization;

namespace SimplArchive.Presentation;

/// <summary>
/// How a <c>DateTime</c>-typed index value (ISO-8601 with an offset, #660) is shown and edited — the one
/// answer both clients must give identically (ADR 0650's rule), because "2026-09-04T12:30:00+00:00" is a
/// WIRE value: shown raw it reads as a date with debris, and until this existed the desktop pane showed
/// exactly that — a moment the user could not read and could not edit without hand-typing an offset.
/// </summary>
/// <remarks>
/// <b>All three methods take the zone, and none has an overload that does not</b> — the viewer's chosen zone
/// (ADR 0801), not the device's. They were on <c>ToLocalTime()</c> until #1315, which was at least
/// self-consistent; converting only the read half would have been worse than leaving it, because a pane that
/// SHOWS one clock and SAVES another stores a value hours from what the user read and neither half looks
/// wrong on its own. So the pair moves together, and the zone rides in the signature so the compiler
/// enumerates the call sites.
/// </remarks>
public static class IndexInstant
{
    /// <summary>The stored value for display: the instant on the VIEWER's clock, minute precision.</summary>
    /// <remarks>Anything that does not parse is returned as it stands — display never invents a value.</remarks>
    public static string Display(string value, TimeZoneInfo zone) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant.InZone(zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value;

    /// <summary>The stored value split for a date picker + a time picker, on the viewer's clock.</summary>
    public static (DateTime? Date, TimeSpan? Time) Split(string? value, TimeZoneInfo zone) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? (instant.InZone(zone).Date, instant.InZone(zone).TimeOfDay)
            : (null, null);

    /// <summary>
    /// The pickers' answer back into the stored shape: ISO-8601 carrying the VIEWER's offset — a real
    /// instant, which is what the type promises (#660). No date means no value; a date without a time means
    /// midnight, because a picker pair half-filled is a person who chose the day and left the clock alone.
    /// </summary>
    public static string? Compose(DateTime? date, TimeSpan? time, TimeZoneInfo zone)
    {
        if (date is not { } day)
        {
            return null;
        }

        var local = day.Date + (time ?? TimeSpan.Zero);
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
