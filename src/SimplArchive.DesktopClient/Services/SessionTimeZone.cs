using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The zone this session displays stored instants in (#1254) — the signed-in user's preference, or the
/// machine's own zone when they have not set one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambient on purpose, and safe here for a reason that does not generalise.</b> A desktop process serves
/// exactly one signed-in user, so "the display zone" genuinely is one value for the whole app — and the places
/// that need it are row view-models built in bulk, which have no constructor a dependency could ride in on
/// (<c>NodeViewModel</c> is created in half a dozen parsers). Threading a zone through every one of them would
/// be the "silent omission" shape ADR 0730 warns about: a forgotten site would not fail to compile, it would
/// quietly show UTC beside rows showing local.
/// </para>
/// <para>
/// <b>The same static in the Api would be a cross-tenant leak</b>, which is why the rule itself lives in
/// <see cref="DisplayZone"/> (a pure function in Presentation, which the Api references) and only the HOLDER
/// lives here. One slot shared by many tenants is the shape that looks correct in every test and serves one
/// tenant's value to another in production.
/// </para>
/// <para>
/// It starts at the machine's zone so the very first render — before "me" has been read — is already sensible
/// rather than UTC, and <see cref="Set"/> replaces it once the preference arrives.
/// </para>
/// </remarks>
public static class SessionTimeZone
{
    /// <summary>The zone to display in. Never null; the machine's own zone until the preference is known.</summary>
    public static TimeZoneInfo Current { get; private set; } = TimeZoneInfo.Local;

    /// <summary>The IANA id of <see cref="Current"/> — what rides in <c>X-Time-Zone</c> on a search.</summary>
    public static string IanaId => DisplayZone.IanaId(Current);

    /// <summary>Applies the stored preference; a null or unknown id falls back to the machine's zone.</summary>
    public static void Set(string? preferredId) => Current = DisplayZone.Resolve(preferredId);

    /// <summary>Back to the machine's zone — on sign-out, so the next user does not inherit this one's choice.</summary>
    public static void Reset() => Current = TimeZoneInfo.Local;
}
