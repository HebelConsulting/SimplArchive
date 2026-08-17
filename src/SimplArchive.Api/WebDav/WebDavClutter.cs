
namespace SimplArchive.Api.WebDav;

// What a mounted OS volume writes that the archive must never store (issue #466 moved this out of the
// middleware; ADRs "WebDAV clutter filter" / 0508 / ".crdownload staging" own the rules). Pure name tests —
// no I/O, no state — shared by the listing, PUT and MOVE paths.
internal static class WebDavClutter
{
    // Cached preview/text-layout artifacts + staged mask sidecars never appear as intray items (ADR "Avoid inbox
    // preview litter").
    internal static bool IsIntrayLitter(string name) =>
        name.Contains(".preview.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".textlayout.json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mask.json", StringComparison.OrdinalIgnoreCase);

    // OS metadata clutter written by Finder / Explorer when browsing a mounted WebDAV volume — never wanted
    // ANYWHERE (repo, Intray, or Check-out): macOS AppleDouble (._*), .DS_Store, the Spotlight/Trash/fsevents
    // dot-dirs, and Windows Thumbs.db / desktop.ini. Silently accepted-and-discarded (a copy in Finder/Explorer
    // succeeds; the junk is never stored). ADR "WebDAV clutter filter".
    internal static readonly HashSet<string> OsClutterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", ".localized", ".apdisk", ".VolumeIcon.icns", "Thumbs.db", "ehthumbs.db", "desktop.ini",
    };

    internal static bool IsOsClutter(string name) =>
        name.StartsWith("._", StringComparison.Ordinal)
        || OsClutterNames.Contains(name)
        || name.StartsWith(".Spotlight-V100", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".Trashes", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".fseventsd", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".TemporaryItems", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".DocumentRevisions-V100", StringComparison.OrdinalIgnoreCase);

    // Transient / partial-download / editor-temp files. Legitimate in the Intray / Check-out staging areas (e.g. an
    // in-progress download), but should NOT land in the permanent repository. ADR "WebDAV clutter filter".
    internal static readonly HashSet<string> TransientExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".tmp", ".temp", ".swp", ".swx",
    };

    internal static bool IsTransientClutter(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) // Office lock/temp files
        || TransientExtensions.Contains(Path.GetExtension(name));

    // Browser in-progress-download temp files (ADR "WebDAV .crdownload staging"). When a browser downloads a file
    // INTO a mounted WebDAV folder it writes the bytes to one of these (Chromium .crdownload, Firefox .part,
    // IE/legacy-Edge .partial, legacy Opera .dltemp) and renames it to the final name on completion. Rather than
    // dropping these as clutter (losing the bytes) or letting the OS's zero-byte placeholder create an empty
    // document, we STAGE them in a per-user temp area and materialize the real document on the completing MOVE.
    // (Safari's .download is a directory bundle, out of scope; other transient/editor temps stay dropped clutter.)
    internal static readonly HashSet<string> DownloadTempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".dltemp",
    };

    internal static bool IsDownloadTemp(string name) => DownloadTempExtensions.Contains(Path.GetExtension(name));

    // The caller's checked-out documents, shown by name; the working copy is the cloud stash if present, else
    // the current confirmed version (ADR "Document check-out / check-in" stash).
    // Office/LibreOffice owner + lock files (~$name / .~lock.name#) — hidden from the special-folder listings so
    // they don't clutter the view while an edit is in flight (ADR 0508). They still PUT/DELETE like any file.
    internal static bool IsLockFile(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal)
        || (name.StartsWith(".~lock.", StringComparison.Ordinal) && name.EndsWith("#", StringComparison.Ordinal));
}
