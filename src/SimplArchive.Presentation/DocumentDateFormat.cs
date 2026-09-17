using System.Globalization;

namespace SimplArchive.Presentation;

/// <summary>
/// How a document's date — and its OPTIONAL time-of-day — is shown and typed, the one answer both clients
/// must give identically (ADR 0650). The pair is <b>stored in UTC</b> and <b>shown in the viewer's zone</b>
/// (#1254): the date is a <c>DateOnly</c> ("yyyy-MM-dd"), the time a <c>TimeOnly?</c> ("HH:mm") or null when
/// the date carries no time. Entry is KEYBOARD-FIRST (the standing principle): the user types the time, and
/// <see cref="TryParseTypedTime"/> accepts the obvious spellings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method that touches the time takes a zone, and there is no overload that does not.</b> The two
/// directions have to agree — a pane that SHOWS 00:30 local and SAVES what the user types as UTC stores a
/// value two hours from what they read, and neither half looks wrong on its own. Forcing the zone through the
/// signature makes the compiler enumerate the call sites, which is the only thing that does: a defaulted
/// overload would leave the forgotten site showing a different time for the same document than the pane beside
/// it, silently and forever. (The same lesson as #854: the compiler enumerates the server's row types and
/// nothing enumerates a client's — so make it a parameter, not a default.)
/// </para>
/// <para>
/// <b>A date with NO time is not converted, in either direction.</b> "Filed on the 15th" is a calendar day, not
/// an instant; giving it a notional midnight to convert would make it read as the 14th for anyone west of the
/// server — inventing a bug in order to fix one that was never there.
/// </para>
/// <para>
/// <b>Why this arithmetic is here and not shared with <c>Domain.DocumentInstant</c>.</b> The server needs the
/// pair as an instant (indexing) and a caller's calendar day as a UTC range (search); a client needs the pair
/// converted to and from a local pair. They overlap on one expression — "the pair is a UTC instant" — not on a
/// body of logic, and Presentation is a dependency-free leaf by design. Adding an edge from Infrastructure to
/// Presentation to share that one line would cost more than it saves; if the two ever disagree, the E2E tests
/// that read a stored value back are what say so.
/// </para>
/// </remarks>
public static class DocumentDateFormat
{
    /// <summary>
    /// The stored UTC pair as the viewer reads it: "2026-09-06", or "2026-09-06 11:50 +02:00" when a time is
    /// present — the date moves with the time, because the two columns are ONE instant.
    /// </summary>
    public static string Display(DateOnly utcDate, TimeOnly? utcTime, TimeZoneInfo zone)
    {
        var (date, time) = ToZone(utcDate, utcTime, zone);

        return time is { } t
            ? $"{date:yyyy-MM-dd} {t.ToString("HH:mm", CultureInfo.InvariantCulture)} {Marker(utcDate, utcTime, zone)}"
            : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The same, from the wire strings the API sends (date "yyyy-MM-dd", time "HH:mm" or null/blank).
    /// Anything unparseable falls back to the raw date string — display never invents a value.</summary>
    public static string Display(string? date, string? time, TimeZoneInfo zone)
    {
        if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return date ?? string.Empty;
        }

        return TryParseTypedTime(time, out var t) ? Display(d, t, zone) : Display(d, null, zone);
    }

    /// <summary>
    /// The stored UTC pair as the local pair the edit fields are filled with. A date-only pair passes through.
    /// </summary>
    public static (DateOnly Date, TimeOnly? Time) ToZone(DateOnly utcDate, TimeOnly? utcTime, TimeZoneInfo zone)
    {
        if (utcTime is not { } time)
        {
            return (utcDate, null);
        }

        var local = TimeZoneInfo.ConvertTime(
            new DateTimeOffset(utcDate.ToDateTime(time), TimeSpan.Zero), zone);
        return (DateOnly.FromDateTime(local.DateTime), TimeOnly.FromDateTime(local.DateTime));
    }

    /// <summary>
    /// The edit fields' local pair back into the stored UTC pair. A date-only pair passes through.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="ToZone"/>, and the half that is easy to skip: showing local while saving the
    /// typed value verbatim shifts every document by the offset each time somebody opens and saves it, which
    /// compounds. <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> answers for a wall clock that does not
    /// exist (the hour a spring-forward skips) with the pre-transition offset — the instant that yields is the
    /// first real one after the gap, which is the sensible reading of a time the user typed anyway.
    /// </remarks>
    public static (DateOnly Date, TimeOnly? Time) ToUtc(DateOnly localDate, TimeOnly? localTime, TimeZoneInfo zone)
    {
        if (localTime is not { } time)
        {
            return (localDate, null);
        }

        var wall = DateTime.SpecifyKind(localDate.ToDateTime(time), DateTimeKind.Unspecified);
        var utc = new DateTimeOffset(wall, zone.GetUtcOffset(wall)).UtcDateTime;
        return (DateOnly.FromDateTime(utc), TimeOnly.FromDateTime(utc));
    }

    /// <summary>
    /// What a displayed time is labelled with: <c>UTC</c> at a zero offset, otherwise the ISO offset
    /// (<c>+02:00</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker is not decoration. A user may deliberately set their display zone to UTC (#1254 offers that
    /// as a self-service choice), and a screenshot or a bug report has to be readable by somebody who is not
    /// sitting in the reporter's zone — "09:50" alone is a number nobody else can place.
    /// </para>
    /// <para>
    /// An offset rather than an abbreviation, because .NET cannot portably produce "CEST": the display names it
    /// has are culture-dependent prose, and a zone's own abbreviation is not in the API at all. The offset is
    /// culture-free, unambiguous, and the same width every time — which matters in a list column.
    /// </para>
    /// <para>
    /// It is computed AT THE INSTANT rather than from the zone's base offset, so a summer document says +02:00
    /// and a winter one says +01:00 in the very same list. London in winter reads "UTC", which is correct.
    /// </para>
    /// </remarks>
    public static string Marker(DateOnly utcDate, TimeOnly? utcTime, TimeZoneInfo zone)
    {
        var offset = zone.GetUtcOffset(new DateTimeOffset(utcDate.ToDateTime(utcTime ?? TimeOnly.MinValue), TimeSpan.Zero));

        return offset == TimeSpan.Zero
            ? "UTC"
            : (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The stored UTC pair as the EDIT FIELDS carry it — a date picker's <c>DateTime?</c> and a typed
    /// "HH:mm" string — converted into the viewer's zone.
    /// </summary>
    /// <remarks>
    /// The plumbing shape, shared because both clients happen to have exactly it: Blazor's
    /// <c>MudDatePicker</c> and Avalonia's <c>CalendarDatePicker</c> both bind a <c>DateTime?</c>, and both
    /// panes type the time into a plain text box (the typed-not-picked principle). One implementation rather
    /// than two that drift.
    /// </remarks>
    public static (DateTime? Date, string? Time) FieldsInZone(DateTime? utcDate, string? utcTime, TimeZoneInfo zone)
    {
        if (utcDate is not { } date || !TryParseTypedTime(utcTime, out var parsed) || parsed is null)
        {
            // No date, or no time to move it by: the fields stand as they are. A date with no time is a
            // calendar day, not an instant.
            return (utcDate, utcTime);
        }

        var (localDate, localTime) = ToZone(DateOnly.FromDateTime(date), parsed, zone);
        return (localDate.ToDateTime(TimeOnly.MinValue), FormatTime(localTime));
    }

    /// <summary>
    /// The edit fields' local pair back into the wire's UTC pair — the inverse of <see cref="FieldsInZone"/>.
    /// </summary>
    /// <remarks>
    /// Returns the date as a wire string because that is what the save body carries. When there is nothing to
    /// convert — no date, or a blank/unparseable time — both halves come back untouched, so a caller's
    /// existing "fall back to the baseline" handling keeps working unchanged.
    /// </remarks>
    public static (string? Date, string? Time) FieldsInUtc(DateTime? localDate, string? localTime, TimeZoneInfo zone)
    {
        var wireDate = localDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (localDate is not { } date || !TryParseTypedTime(localTime, out var parsed) || parsed is null)
        {
            return (wireDate, localTime);
        }

        var (utcDate, utcTime) = ToUtc(DateOnly.FromDateTime(date), parsed, zone);
        return (utcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), FormatTime(utcTime));
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
