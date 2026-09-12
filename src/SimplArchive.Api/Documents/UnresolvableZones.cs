using Microsoft.Extensions.Logging;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Reports a time zone this host cannot resolve — once per zone, at Warning (#1138).
/// </summary>
/// <remarks>
/// <para>
/// Falling back to the host's own zone keeps an item indexed and findable rather than dropping its time,
/// which is the right trade. What was wrong is that it happened in SILENCE: an Alpine image without tzdata
/// resolves NO IANA id, so every zoned appointment was stored in the container's zone — 10:00 Europe/Zurich
/// became 10:00Z rather than 08:00Z — and a healthy-looking system judged every conflict against the wrong
/// instant. ADR 0626: where we silently degrade something the caller cannot see, say so and name it.
/// </para>
/// <para>
/// Once per zone, because the alternative is a line per calendar item — a flood nobody reads is the same as
/// no warning at all. The set is small and bounded by how many zones a tenant's entries actually name.
/// </para>
/// </remarks>
public static class UnresolvableZones
{
    private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once at startup; absent in tests, where the warning has nowhere useful to go.</summary>
    public static ILogger? Logger { get; set; }

    /// <summary>
    /// Says at STARTUP when this host carries no IANA database at all, rather than waiting for the first
    /// mis-stored appointment to reveal it.
    /// </summary>
    /// <remarks>
    /// The check is "does a zone every installation needs resolve?", not a count of the database: a host with
    /// one broken zone is a curiosity, while a host with NONE mis-stores every zoned entry it will ever see.
    /// </remarks>
    public static void WarnIfDatabaseMissing()
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        }
        catch (Exception)
        {
            Logger?.LogWarning(
                "This host carries no IANA time-zone database, so EVERY appointment naming a zone will be "
                + "stored in the host's own zone and sit at the wrong instant. Install it (the 'tzdata' "
                + "package on Alpine).");
        }
    }

    public static void Warn(string timeZoneId)
    {
        lock (Reported)
        {
            if (!Reported.Add(timeZoneId))
            {
                return;
            }
        }

        Logger?.LogWarning(
            "Time zone {TimeZoneId} cannot be resolved on this host, so times naming it are being stored in "
            + "the host's own zone instead — every such entry will sit at the wrong instant. Install the IANA "
            + "time-zone database (the 'tzdata' package on Alpine). Turn on Trace to see the exchanges that "
            + "named it.",
            timeZoneId);
    }
}
