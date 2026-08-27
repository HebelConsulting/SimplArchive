
using System.Text.RegularExpressions;

namespace SimplArchive.Api.WebDav;

// What a mounted OS volume writes that the archive must never store (issue #466 moved this out of the
// middleware; ADRs "WebDAV clutter filter" / 0508 / ".crdownload staging" own the rules). Pure name tests —
// no I/O, no state — shared by the listing, PUT and MOVE paths.
internal static partial class WebDavClutter
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

    // A word processor's atomic replace on macOS: it creates a SIBLING COLLECTION named `<file>.sb-<hex>-<rand>`,
    // works inside it, then swaps. Never a document, and never ours to keep — but also never something to refuse
    // (#764).
    //
    // BOTH obvious answers are wrong, which is why this rule exists rather than a line in one of the sets above.
    // Answering 201 and materialising it leaves a version-less Document behind, and a version-less Document is
    // drawn as a FOLDER — three saves, three phantom folders. Answering 403 is worse: the editor concludes the
    // volume cannot do an atomic replace, rolls back, and DELETES THE ORIGINAL FILE. In the Intray, where items
    // are object-storage keys with no soft-delete, that is unrecoverable — observed destroying a file that a GET
    // had served seconds earlier.
    //
    // So it is accepted (201) and discarded, exactly as an OS-junk directory is. The suffix is matched rather
    // than the stem, because the collection is named after whatever file is being replaced.
    [GeneratedRegex(@"\.sb-[0-9a-fA-F]+-[A-Za-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSaveTempSuffix();

    internal static bool IsSafeSaveTemp(string name) => SafeSaveTempSuffix().IsMatch(name);

    /// <summary>True when the path IS a safe-save collection, or lies inside one.</summary>
    /// <remarks>
    /// Takes the whole path rather than a name, so no caller has to index it. That is not tidiness: the first
    /// version of this fix asked <c>IsSafeSaveTemp(segments[^1])</c> at three call sites, and on the MOUNT ROOT
    /// — where the segment list is EMPTY — that threw, so every client's first PROPFIND returned 500 and the
    /// volume could not be mounted at all. A helper that cannot be handed an empty list wrongly is the fix;
    /// three guards that each have to remember are not.
    /// </remarks>
    internal static bool IsSafeSaveScope(IReadOnlyList<string> segments) =>
        segments.Count > 0 && (IsSafeSaveTemp(segments[^1]) || IsUnderSafeSaveTemp(segments));

    /// <summary>True when any ANCESTOR segment of a path is a safe-save collection.</summary>
    /// <remarks>
    /// The leaf is the wrong thing to test here, and testing it is what left the fix half-done (#762). With a
    /// browser download temp the FILE carries the marker; with a safe-save the marker is on the PARENT
    /// DIRECTORY, and the file inside usually has the original's exact name. So `IsSafeSaveTemp(segments[^1])`
    /// never matches the write that actually happens, which is the one inside the collection.
    ///
    /// The ancestors, not the whole path: a request FOR the collection itself is about the collection, while a
    /// request for something inside it is a write that has to be staged.
    /// </remarks>
    internal static bool IsUnderSafeSaveTemp(IReadOnlyList<string> segments)
    {
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (IsSafeSaveTemp(segments[i]))
            {
                return true;
            }
        }

        return false;
    }

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
