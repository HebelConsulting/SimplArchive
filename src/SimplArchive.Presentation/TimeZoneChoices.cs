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

    /// <summary>
    /// This machine's own zone, as an IANA id — what a NEW appointment is stamped with (#1126).
    /// </summary>
    /// <remarks>
    /// <para>
    /// New entries used to be left floating, deliberately (ADR 0631 decision 5): a time with no zone is
    /// "09:00 wherever you are", and stamping one inferred from the machine is how a floating time stops
    /// floating. That is superseded for CREATE only — a person filing a meeting almost always means their own
    /// zone, and the blank entry stays at the top of the list so a floating time is still one click away.
    /// EDITING is untouched: an entry opens with whatever it was stored with, blank included, so opening an
    /// old floating entry and saving it cannot silently stamp a zone it never had.
    /// </para>
    /// <para>
    /// Shared rather than read from <c>TimeZoneInfo.Local</c> at each call site, for this project's usual
    /// reason and one specific to zones: the conversion to IANA must be the SAME one the list went through,
    /// or a Windows host pre-selects a value that is not in its own picker.
    /// </para>
    /// </remarks>
    public static string Local() => Iana(TimeZoneInfo.Local.Id);

    /// <summary>The IANA spelling of a zone id, or the id unchanged when it is already one.</summary>
    public static string Iana(string id) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
}
