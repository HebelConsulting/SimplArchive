using SimplArchive.Presentation;

namespace SimplArchive.Client.Services;

/// <summary>
/// The zone this session displays stored instants in (#1254) — the signed-in user's preference, or the
/// browser's own zone when they have not set one.
/// </summary>
/// <remarks>
/// <para>
/// The desktop's <c>SessionTimeZone</c> in the web client's words, deliberately the same shape (ADR 0511): two
/// clients answering one question identically rather than each inventing its own.
/// </para>
/// <para>
/// <b>Ambient rather than injected, and safe here for a reason that does not generalise.</b> A WASM app
/// instance serves exactly one signed-in user — starting or stopping an impersonation force-reloads the whole
/// app, so there is no moment at which two users share this value. The Api references the same Presentation
/// assembly and must never hold a static like this, which is why the RULE lives in <see cref="DisplayZone"/>
/// and only the holder lives here.
/// </para>
/// <para>
/// It starts at the browser's zone so the first render — before "me" has been read — is already sensible
/// rather than UTC, and <see cref="Set"/> replaces it once the preference arrives.
/// </para>
/// </remarks>
public static class SessionTimeZone
{
    /// <summary>The zone to display in. Never null; the browser's own zone until the preference is known.</summary>
    public static TimeZoneInfo Current { get; private set; } = TimeZoneInfo.Local;

    /// <summary>The IANA id of <see cref="Current"/> — what rides in <c>X-Time-Zone</c> on a search.</summary>
    public static string IanaId => DisplayZone.IanaId(Current);

    /// <summary>Applies the stored preference; a null or unknown id falls back to the browser's zone.</summary>
    public static void Set(string? preferredId) => Current = DisplayZone.Resolve(preferredId);
}
