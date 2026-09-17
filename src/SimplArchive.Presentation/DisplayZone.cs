namespace SimplArchive.Presentation;

/// <summary>
/// Which zone a client shows stored instants in (#1254): the user's own preference when they have set one,
/// otherwise the device's zone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null means "follow my device", and only a client can resolve that.</b> The server stores the preference
/// and hands it back as it is — it must never substitute its own zone, or every user would inherit whatever
/// zone the container happens to run in. So the fallback lives here, on the side that actually knows.
/// </para>
/// <para>
/// <b>Shared for this project's usual reason</b> (ADR 0650): two clients resolving the same preference two
/// ways is two answers to one question, and only one of them would ever get fixed.
/// </para>
/// <para>
/// <b>This class is a pure function, and deliberately holds no state.</b> The resolved zone is ambient in a
/// client — one process serves exactly one signed-in user — but the Api references this assembly too, and a
/// static "current zone" reachable from there is a cross-tenant leak of the kind that looks fine in every test:
/// one slot, many tenants, last writer wins. Each client keeps its own holder; the rule stays here.
/// </para>
/// </remarks>
public static class DisplayZone
{
    /// <summary>
    /// The zone to display in, from the stored preference (an IANA id, or null/blank for "follow my device").
    /// </summary>
    /// <remarks>
    /// An unknown id falls back to the device rather than throwing: the preference is data that outlives the
    /// host it was set on — a zone can be renamed, and a client can run on a machine whose database is older
    /// than the one the preference was chosen on. A stale preference should cost the user their override, not
    /// their document list.
    /// </remarks>
    public static TimeZoneInfo Resolve(string? preferredId)
    {
        if (string.IsNullOrWhiteSpace(preferredId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(preferredId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// The IANA id to tell the server the caller is in — what rides in <c>X-Time-Zone</c> on a search.
    /// </summary>
    /// <remarks>
    /// Converted, because <see cref="TimeZoneInfo.Local"/> on Windows answers with a Windows id
    /// ("W. Europe Standard Time"), which no other party in this system can resolve — the same trap
    /// <see cref="TimeZoneChoices"/> exists to close for calendar TZIDs.
    /// </remarks>
    public static string IanaId(TimeZoneInfo zone) => TimeZoneChoices.Iana(zone.Id);
}
