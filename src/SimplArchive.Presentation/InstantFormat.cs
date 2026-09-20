using System.Globalization;

namespace SimplArchive.Presentation;

/// <summary>
/// How an INSTANT — a filing time, a deletion, a chat message, an audit event — is shown to a viewer, the one
/// answer both clients must give identically (ADRs 0650/0651/0511). The value is stored and transported as a
/// true instant (ADR 0802, written as Zulu) and shown in the viewer's chosen zone (ADR 0801).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> ADR 0801 gave every user a display time zone and routed the document
/// date/time pair through <see cref="DocumentDateFormat"/> — but it scoped itself to that pair, and the word
/// "Created" never appears in it. So every OTHER instant kept being rendered with
/// <c>DateTimeOffset.ToLocalTime()</c>, which is the DEVICE's zone and cannot see the preference. The two sat
/// side by side in one details pane: switching from Europe/Zurich to Europe/Bucharest moved the document date
/// by an hour and left the filing date exactly where it was — reported from the kiosk, #1315.
/// </para>
/// <para>
/// <b>The zone is in the signature, and there is no overload without it.</b> Copied deliberately from
/// <see cref="DocumentDateFormat"/>, whose own remarks explain why: a defaulted overload leaves the forgotten
/// call site showing a different time for the same thing than the row beside it, silently and forever, and the
/// compiler is the only thing that reliably enumerates client call sites. That rule is the entire reason the
/// document-date path was right while everything around it was wrong — so it is the rule, not the exception.
/// </para>
/// <para>
/// <b>The offset marker is not decoration.</b> "2026-09-20 18:47" is unreadable without knowing whose clock it
/// is: a viewer whose device and preference differ has no way to tell which one they are looking at, which is
/// exactly how the defect above survived — it renders perfectly plausibly in the wrong zone. The marker is the
/// same one <see cref="DocumentDateFormat"/> already shows beside a document time, so the two rows of one pane
/// read alike.
/// </para>
/// </remarks>
public static class InstantFormat
{
    /// <summary>
    /// The instant as the viewer reads it: "2026-09-20 18:47 +02:00".
    /// </summary>
    public static string Display(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);

        return $"{local:yyyy-MM-dd HH:mm} {Marker(instant, zone)}";
    }

    /// <summary>The same, for a value that may be absent — an empty string rather than a fabricated date.</summary>
    public static string Display(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? Display(value, zone) : string.Empty;

    /// <summary>
    /// The instant as the viewer reads it, without the zone marker — for a surface that already states the
    /// zone once for the whole view, or where the row is too narrow to carry it.
    /// </summary>
    /// <remarks>
    /// Separate method rather than a flag: a caller that omits the marker is making a claim about its OWN
    /// layout, and that claim should be visible at the call site rather than buried in a boolean argument
    /// whose meaning a reader has to look up.
    /// </remarks>
    public static string Bare(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Bare(DateTimeOffset, TimeZoneInfo)"/>
    public static string Bare(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? Bare(value, zone) : string.Empty;

    /// <summary>
    /// The instant converted into the viewer's zone, for a caller that does its own formatting — a date-only
    /// column, a relative "3 minutes ago", a grouping key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Present so that such a caller has somewhere to go OTHER than <c>ToLocalTime()</c>. Without it the sweep
    /// would fix the rows that happen to want this class's format and leave every other one reaching for the
    /// device zone again, which is how the defect spread in the first place.
    /// </para>
    /// <para>
    /// An EXTENSION, deliberately, so that correcting a site is <c>.ToLocalTime()</c> →
    /// <c>.InZone(SessionTimeZone.Current)</c> and nothing else moves. A sweep that also changed each row's
    /// format would be two changes wearing one diff: the zone is the defect, the format is not, and mixing
    /// them makes every assertion that breaks ambiguous between "we fixed it" and "we broke it".
    /// </para>
    /// </remarks>
    public static DateTimeOffset InZone(this DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone);

    /// <inheritdoc cref="InZone(DateTimeOffset, TimeZoneInfo)"/>
    public static DateTimeOffset? InZone(this DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, zone) : null;

    /// <summary>The viewer's offset at that instant — "+02:00", or "UTC".</summary>
    public static string Marker(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var offset = zone.GetUtcOffset(instant);

        return offset == TimeSpan.Zero
            ? "UTC"
            : (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
}
