using System.Globalization;

namespace SimplArchive.Presentation;

/// <summary>
/// How a document's date — and its OPTIONAL UTC time-of-day — is shown and typed, the one answer both
/// clients must give identically (ADR 0650). The date is a <c>DateOnly</c> ("yyyy-MM-dd"); the time is a
/// <c>TimeOnly?</c> in UTC ("HH:mm") or null when the date carries no time. Display appends an explicit
/// <c>UTC</c> marker so a time is never read as local. Entry is KEYBOARD-FIRST (the standing principle): the
/// user types the time, and <see cref="TryParseTypedTime"/> accepts the obvious spellings.
/// </summary>
public static class DocumentDateFormat
{
    /// <summary>"2026-09-06", or "2026-09-06 09:50 UTC" when a time is present.</summary>
    public static string Display(DateOnly date, TimeOnly? time) =>
        time is { } t
            ? $"{date:yyyy-MM-dd} {t.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC"
            : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The same, from the wire strings the API sends (date "yyyy-MM-dd", time "HH:mm" or null/blank).
    /// Anything unparseable falls back to the raw date string — display never invents a value.</summary>
    public static string Display(string? date, string? time)
    {
        if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return date ?? string.Empty;
        }

        return TryParseTypedTime(time, out var t) ? Display(d, t) : Display(d, null);
    }

    /// <summary>The canonical wire form of a time, or null.</summary>
    public static string? FormatTime(TimeOnly? time) => time?.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a TYPED time leniently (the keyboard-first principle): blank → null (the "delete the time"
    /// state, a successful parse); otherwise <c>09:50</c>, <c>9:50</c>, <c>0950</c>, <c>950</c>, or
    /// <c>9.50</c> → 09:50. Returns false only for input that is present but not a valid 24-hour time.
    /// </summary>
    public static bool TryParseTypedTime(string? typed, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(typed))
        {
            return true; // blank is a valid "no time"
        }

        var s = typed.Trim().Replace('.', ':');

        // Colon form: H:mm / HH:mm.
        if (s.Contains(':'))
        {
            if (TimeOnly.TryParseExact(s, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                time = parsed;
                return true;
            }

            return false;
        }

        // Bare digits: 950 / 0950 (HMM / HHMM); 3 or 4 digits only.
        if (s.Length is 3 or 4 && s.All(char.IsAsciiDigit))
        {
            var minutes = int.Parse(s[^2..], CultureInfo.InvariantCulture);
            var hours = int.Parse(s[..^2], CultureInfo.InvariantCulture);
            if (hours is >= 0 and < 24 && minutes is >= 0 and < 60)
            {
                time = new TimeOnly(hours, minutes);
                return true;
            }
        }

        return false;
    }
}
