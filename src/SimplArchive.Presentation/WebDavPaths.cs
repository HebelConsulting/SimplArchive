namespace SimplArchive.Presentation;

/// <summary>
/// Where a special folder sits inside the WebDAV mount, composed once for both clients.
/// </summary>
/// <remarks>
/// <para>
/// The mount is the user's tree (ADR 0509), so the Intray and Check-out folders live under the PERSONAL SPACE —
/// and a personal space is named after its owner (ADR 0671), not "Personal". Four call sites across the two
/// clients had the old literal baked in, so every one of them addressed a folder that does not exist: the
/// desktop's "open WebDAV folder" simply did nothing, and the same was true of the web's.
/// </para>
/// <para>
/// One function rather than a constant per tab, because the segment is not knowable at compile time — it is
/// whatever that user's space is called. Shared for the reason <see cref="FilingRoots"/> is: two copies of a
/// rule about where things live is how the two clients come to disagree about it (ADR 0689).
/// </para>
/// </remarks>
public static class WebDavPaths
{
    /// <summary>The mount-relative path of a folder inside the caller's personal space.</summary>
    /// <param name="personalSpaceName">The personal space's own name, as the server gives it.</param>
    /// <param name="leaf">The folder within it — "Intray" or "Check-out".</param>
    /// <returns>
    /// The path, or an EMPTY string when the name is not known. Empty means "open the mount root", which is
    /// what the callers already do for the whole-archive case — a user who lands one level up can navigate,
    /// whereas one sent to a folder that does not exist is told the volume is broken.
    /// </returns>
    public static string InPersonalSpace(string? personalSpaceName, string leaf)
        => string.IsNullOrWhiteSpace(personalSpaceName) || string.IsNullOrWhiteSpace(leaf)
            ? string.Empty
            : $"{personalSpaceName.Trim('/')}/{leaf.Trim('/')}";
}
