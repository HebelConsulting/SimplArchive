namespace SimplArchive.Presentation;

/// <summary>
/// The time zones an appointment editor offers, named the way a calendar file names them.
/// </summary>
/// <remarks>
/// <para>
/// <b>IANA ids, always — even on Windows.</b> A TZID in an <c>.ics</c> is an IANA id (<c>Europe/Zurich</c>),
/// and <see cref="TimeZoneInfo.GetSystemTimeZones"/> answers with WINDOWS ids on Windows
/// (<c>W. Europe Standard Time</c>). Offering the machine's own spelling would write a TZID that every other
/// calendar client on earth cannot resolve — an interop break visible only to whoever opens the entry next,
/// on a different platform. So the list is converted, once, here.
/// </para>
/// <para>
/// Shared for the reason the rest of this project's display rules are: two clients offering two different sets
/// of zone names is two different answers to one question, and only one of them would get fixed.
/// </para>
/// </remarks>
public static class TimeZoneChoices
{
    /// <summary>Every zone this host knows, as IANA ids, sorted and de-duplicated.</summary>
    /// <remarks>
    /// De-duplicated because the Windows→IANA mapping is many-to-one: several Windows zones map to the same
    /// IANA id, so the raw conversion yields a list with repeats — which in a picker reads as a bug.
    /// </remarks>
    public static IReadOnlyList<string> All() =>
    [
        .. TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => Iana(zone.Id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal),
    ];

    /// <summary>The IANA spelling of a zone id, or the id unchanged when it is already one.</summary>
    public static string Iana(string id) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
}
