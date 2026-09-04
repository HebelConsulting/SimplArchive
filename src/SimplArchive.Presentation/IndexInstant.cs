using System.Globalization;

namespace SimplArchive.Presentation;

/// <summary>
/// How a <c>DateTime</c>-typed index value (ISO-8601 with an offset, #660) is shown and edited — the one
/// answer both clients must give identically (ADR 0650's rule), because "2026-09-04T12:30:00+00:00" is a
/// WIRE value: shown raw it reads as a date with debris, and until this existed the desktop pane showed
/// exactly that — a moment the user could not read and could not edit without hand-typing an offset.
/// </summary>
public static class IndexInstant
{
    /// <summary>The stored value for display: the LOCAL wall clock of the instant, minute precision.</summary>
    /// <remarks>Anything that does not parse is returned as it stands — display never invents a value.</remarks>
    public static string Display(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value;

    /// <summary>The stored value split for a date picker + a time picker, in local time.</summary>
    public static (DateTime? Date, TimeSpan? Time) Split(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? (instant.ToLocalTime().Date, instant.ToLocalTime().TimeOfDay)
            : (null, null);

    /// <summary>
    /// The pickers' answer back into the stored shape: ISO-8601 carrying the LOCAL offset — a real instant,
    /// which is what the type promises (#660). No date means no value; a date without a time means midnight,
    /// because a picker pair half-filled is a person who chose the day and left the clock alone.
    /// </summary>
    public static string? Compose(DateTime? date, TimeSpan? time)
    {
        if (date is not { } day)
        {
            return null;
        }

        var local = day.Date + (time ?? TimeSpan.Zero);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
