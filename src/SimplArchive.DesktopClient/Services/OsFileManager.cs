using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

// Mounts a WebDAV folder and opens it in the OS file manager (ADR "Desktop inbox via WebDAV" + "Fix
// open-in-file-manager"). Native WebDAV mounting is OS-specific:
//   • macOS  → one osascript that mounts the volume AND opens it in Finder + brings Finder forward. `mount
//     volume` alone mounts silently WITHOUT opening a window (the original bug — nothing appeared to happen),
//     so we capture the mounted disk and `open` it. osascript reports a mount failure via a non-zero exit.
//   • Windows → `explorer` on the DavWWWRoot UNC path (the WebClient redirector mounts + opens a window).
//   • Linux  → `xdg-open "davs://…"` (GVFS/Nautilus mounts + opens).
// Command construction is a pure function (unit-tested); the launch is async so the mount's credential prompt
// can't freeze the UI thread, and it surfaces failures instead of swallowing them.
public static class OsFileManager
{
    public enum Platform { MacOs, Windows, Linux }

    public static Platform Current =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Platform.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Platform.MacOs
        : Platform.Linux;

    public sealed record OpenResult(bool Success, string? Error);

    // The (executable, argument list) that mounts + opens the given http(s) WebDAV URL. An argument list (not a
    // pre-quoted string) so the multi-line AppleScript with embedded quotes is passed verbatim, no shell escaping.
    public static (string FileName, string[] Arguments) BuildOpenCommand(string httpUrl, Platform platform)
    {
        var uri = new Uri(httpUrl);
        return platform switch
        {
            // Mount, capture the disk, open it in Finder and bring Finder to the front — all in ONE -e script
            // (each -e is a separate context, so the mount + open must share one script to share the `d` variable).
            Platform.MacOs => ("osascript", new[]
            {
                "-e",
                $"set d to (mount volume \"{httpUrl}\")\ntell application \"Finder\"\nactivate\nopen d\nend tell",
            }),
            Platform.Linux => ("xdg-open", new[] { ToDavScheme(uri) }),
            _ => ("explorer.exe", new[] { ToWindowsUnc(uri) }),
        };
    }

    // Runs the mount/open command on a background thread (so the native credential prompt can't block the UI
    // thread) and reports success/failure. On macOS a non-zero osascript exit (e.g. the server is unreachable or
    // auth was cancelled) is surfaced; explorer.exe returns non-zero even on success and xdg-open just hands off,
    // so those are treated as fire-and-forget.
    public static Task<OpenResult> OpenWebDavAsync(string httpUrl)
    {
        var (fileName, arguments) = BuildOpenCommand(httpUrl, Current);
        return RunAsync(fileName, arguments, macOsChecksExit: Current == Platform.MacOs);
    }

    // Runs the mount/open command on a background thread and reports success/failure. On macOS a non-zero
    // osascript exit (server unreachable / auth cancelled) is surfaced; explorer.exe/cmd/xdg-open just hand off.
    private static Task<OpenResult> RunAsync(string fileName, string[] arguments, bool macOsChecksExit)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(fileName)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in arguments)
                {
                    psi.ArgumentList.Add(a);
                }

                using var process = Process.Start(psi);
                if (process is null)
                {
                    return new OpenResult(false, $"Could not start {fileName}.");
                }

                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (macOsChecksExit && process.ExitCode != 0)
                {
                    return new OpenResult(false, string.IsNullOrWhiteSpace(stderr) ? $"Mount failed (exit {process.ExitCode})." : stderr.Trim());
                }

                return new OpenResult(true, null);
            }
            catch (Exception e)
            {
                return new OpenResult(false, e.Message);
            }
        });
    }

    // The (executable, argument list) that opens a SINGLE file inside the WebDAV mount in its native application
    // (ADR 0513, the Check-out Edit button) — mounting the share first if needed. `relativePath` is under the mount
    // root, e.g. "Personal/Check-out/Invoice 2025-001.pdf". Pure (unit-tested); the run is OpenWebDavFileAsync.
    //   • macOS  → osascript: mount the volume, then `open` the POSIX path /Volumes/<name>/<relativePath>.
    //   • Windows → `cmd /c start` on the DavWWWRoot UNC file path (the WebClient redirector fetches + opens it).
    //   • Linux  → `xdg-open` the davs:// file URL (GVFS mounts + opens it in the default app).
    public static (string FileName, string[] Arguments) BuildOpenWebDavFileCommand(string httpBaseUrl, string relativePath, Platform platform)
    {
        var baseUri = new Uri(httpBaseUrl.TrimEnd('/'));
        var rel = relativePath.Trim('/');
        switch (platform)
        {
            case Platform.MacOs:
                var volumeName = baseUri.AbsolutePath.Trim('/').Split('/')[^1];
                var posixPath = $"/Volumes/{volumeName}/{rel}";
                return ("osascript", new[]
                {
                    "-e",
                    $"set d to (mount volume \"{httpBaseUrl.TrimEnd('/')}\")\ndo shell script \"open \" & quoted form of \"{posixPath}\"",
                });
            case Platform.Linux:
                var escaped = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
                return ("xdg-open", new[] { ToDavScheme(baseUri) + "/" + escaped });
            default:
                var unc = ToWindowsUnc(baseUri) + "\\" + rel.Replace('/', '\\');
                return ("cmd.exe", new[] { "/c", "start", "", unc });
        }
    }

    // Opens a single file inside the WebDAV mount in its native application (ADR 0513). Reuses the same background
    // runner as OpenWebDavAsync so the mount's credential prompt can't block the UI thread.
    public static Task<OpenResult> OpenWebDavFileAsync(string httpBaseUrl, string relativePath)
    {
        var (fileName, arguments) = BuildOpenWebDavFileCommand(httpBaseUrl, relativePath, Current);
        return RunAsync(fileName, arguments, macOsChecksExit: Current == Platform.MacOs);
    }

    // Opens a FOLDER inside the single WebDAV mount in the file manager (mounting the volume first) — e.g. the
    // desktop Inbox / Check-out "Open in file manager" buttons open "Personal/Inbox" / "Personal/Check-out"
    // directly, within the one "SimplArchive" mount (ADR 0509). Same mechanism as opening a file: on macOS `open`
    // on a directory lands Finder there, on Windows `start` on the folder UNC opens Explorer, on Linux xdg-open
    // opens the davs:// folder.
    public static Task<OpenResult> OpenWebDavFolderAsync(string httpBaseUrl, string relativeFolder) =>
        OpenWebDavFileAsync(httpBaseUrl, relativeFolder);

    // https://host:443/webdav/Inbox → davs://host:443/webdav/Inbox ; http → dav.

    // ---- Already mounted? and mapping a persistent drive letter (issue #461) ---------------------------

    /// <summary>The volume name the OS shows, fixed by serving the single resource at /SimplArchive (ADR 0509).</summary>
    public const string VolumeName = "SimplArchive";

    /// <summary>
    /// Where the volume is already mounted, or <c>null</c>. This is what lets ONE button do the next useful
    /// thing rather than always the same thing (#461).
    /// </summary>
    /// <remarks>
    /// A filesystem/DriveInfo check, not a network probe: the question is "does the user already have this on
    /// their desktop", and asking the server would answer a different one. Cheap enough to call per render.
    /// </remarks>
    public static string? MountedPath() =>
        Current switch
        {
            Platform.MacOs => System.IO.Directory.Exists($"/Volumes/{VolumeName}") ? $"/Volumes/{VolumeName}" : null,
            Platform.Windows => System.IO.DriveInfo.GetDrives()
                .FirstOrDefault(d => d.DriveType == System.IO.DriveType.Network && VolumeLabelOf(d) == VolumeName)?.Name,
            // gvfs mounts land under a per-user runtime dir and carry the scheme in the directory name.
            _ => GvfsDavMount(),
        };

    private static string? VolumeLabelOf(System.IO.DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (System.IO.IOException)
        {
            return null; // a disconnected mapping still enumerates
        }
    }

    private static string? GvfsDavMount()
    {
        var gvfs = $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}/gvfs";
        return System.IO.Directory.Exists(gvfs)
            ? System.IO.Directory.EnumerateDirectories(gvfs).FirstOrDefault(d => d.Contains("dav", StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>
    /// A free drive letter, preferring S: — then up through Z:, and only then back down from R:. <c>null</c>
    /// when the machine has none left, which is a fallback reason rather than a crash.
    /// </summary>
    /// <remarks>
    /// Searching UP before DOWN matters: the low letters are where a machine's own devices live, so walking
    /// down first would put the volume somewhere surprising on a lightly-used machine. Stops at D: — A:/B: are
    /// floppy-reserved and C: is the system drive, and a network share must never land on either.
    /// </remarks>
    public static char? FirstFreeDriveLetter()
    {
        var taken = System.IO.DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return "STUVWXYZ".Concat("RQPONMLKJIHGFED").Cast<char?>().FirstOrDefault(c => !taken.Contains(c!.Value));
    }

    /// <summary>
    /// Maps the volume to a PERSISTENT drive letter on Windows, so it survives a reboot (#461).
    /// </summary>
    /// <remarks>
    /// <see cref="BuildOpenCommand"/>'s Windows path opens the DavWWWRoot UNC directly, which mounts and opens
    /// in one step but leaves no drive letter and nothing persistent. That is right for "just show me the
    /// files" and wrong for "put my documents on this machine", which is what the ribbon button means — hence a
    /// second command rather than a change to the first.
    ///
    /// Windows only: macOS and Linux have no drive letters, and their mount is already persistent enough
    /// (Finder remembers the server, gvfs remounts on demand).
    /// </remarks>
    public static (string FileName, string[] Arguments)? BuildMapDriveCommand(string httpUrl, string username, string password) =>
        Current is Platform.Windows && FirstFreeDriveLetter() is { } letter
            ? ("net", new[] { "use", $"{letter}:", ToWindowsUnc(new Uri(httpUrl)), $"/user:{username}", password, "/persistent:yes" })
            : null;

    private static string ToDavScheme(Uri uri) =>
        (uri.Scheme == "https" ? "davs://" : "dav://") + uri.Authority + uri.AbsolutePath;

    // https://host:443/webdav/Inbox → \\host@SSL@443\DavWWWRoot\webdav\Inbox ; http → \\host@80\...
    private static string ToWindowsUnc(Uri uri)
    {
        var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
        var host = uri.Scheme == "https" ? $"{uri.Host}@SSL@{port}" : $"{uri.Host}@{port}";
        var path = uri.AbsolutePath.Replace('/', '\\');
        return $"\\\\{host}\\DavWWWRoot{path}";
    }
}
