using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

// Mounts a WebDAV folder and opens it in the OS file manager (ADR "Desktop inbox via WebDAV" + "Fix
// open-in-file-manager"). Native WebDAV mounting is OS-specific:
//   • macOS  → one osascript that mounts the volume AND opens it, because `mount volume` alone mounts silently
//     without opening a window (the original bug — nothing appeared to happen). It opens the volume by PATH:
//     `mount volume` returns no value, so capturing its result fails with -2753 "The variable d is not defined",
//     most reliably when the volume is already mounted. osascript reports a mount failure via a non-zero exit.
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
            // macOS MOUNTS ONLY; the caller opens the mount point afterwards (OpenWebDavFolderAsync).
            //
            // Two field failures put the open outside this script. `mount volume` returns NOTHING, so the old
            // `set d to (mount volume …)` failed with -2753 "The variable d is not defined" — most reliably when
            // the volume was already mounted. Opening a path derived from the URL instead then failed with "The
            // file /Volumes/… does not exist", because macOS SUFFIXES a colliding volume name: a second
            // SimplArchive mounts at /Volumes/SimplArchive-1, and no name derived from the URL can know that.
            // The only reliable answer is to ask the OS where it put the mount, which needs the mount to have
            // finished — so it cannot happen inside this script.
            Platform.MacOs => ("osascript", new[] { "-e", $"mount volume \"{httpUrl}\"" }),
            Platform.Linux => ("xdg-open", new[] { ToDavScheme(uri) }),
            _ => ("explorer.exe", new[] { ToWindowsUnc(uri) }),
        };
    }

    // Runs the mount/open command on a background thread (so the native credential prompt can't block the UI
    // thread) and reports success/failure. On macOS a non-zero osascript exit (e.g. the server is unreachable or
    // auth was cancelled) is surfaced; explorer.exe returns non-zero even on success and xdg-open just hands off,
    // so those are treated as fire-and-forget.
    public static Task<OpenResult> OpenWebDavAsync(string httpUrl, nint ownerWindow = 0) => OpenWebDavFolderAsync(httpUrl, "", ownerWindow);

    /// <summary>Mounts if needed, waits for the mount to appear, and opens <paramref name="relativeFolder"/> in it.</summary>
    /// <remarks>
    /// <para>
    /// The open is a separate step from the mount, against the mount point the OS actually chose (see
    /// <see cref="MountedPathFor"/>). Doing it inside the AppleScript meant opening a path guessed from the URL,
    /// which is wrong as soon as a second SimplArchive is mounted and macOS suffixes the name.
    /// </para>
    /// <para>
    /// The wait exists because `mount volume` returns before the mount point is registered — opening immediately
    /// after it produced "The file /Volumes/… does not exist" on a mount that had in fact just succeeded.
    /// </para>
    /// </remarks>
    public static async Task<OpenResult> OpenWebDavFolderAsync(string httpBaseUrl, string relativeFolder, nint ownerWindow = 0)
    {
        var baseUrl = httpBaseUrl.TrimEnd('/');
        var relative = relativeFolder.Trim('/');

        DesktopLog.Debug("WebDAV open: url={BaseUrl} folder={Relative} platform={Platform}", baseUrl, relative, Current);
        if (MountedPathFor(baseUrl) is { } already)
        {
            DesktopLog.Debug("WebDAV open: already mounted at {MountPoint}", already);
        }
        else if (OperatingSystem.IsWindows()) // the analyzer-recognised guard for WindowsDavDrive (CA1416)
        {
            // Map a persistent drive letter via the system credential dialog (#820, WindowsDavDrive). The old
            // path handed explorer.exe the bare DavWWWRoot UNC fire-and-forget — and Explorer's own failure
            // mode for a path it cannot open is showing the DOCUMENTS folder, which is exactly how "mounting
            // is not successful" looked in the field: no error anywhere, the wrong folder on screen.
            if (FirstFreeDriveLetter() is not { } letter)
            {
                DesktopLog.Warn("WebDAV mount: no free drive letter (D:–Z: all taken)");
                return new OpenResult(false, "No free drive letter.");
            }

            var unc = ToWindowsUnc(new Uri(baseUrl));
            DesktopLog.Debug("WebDAV mount: mapping {Letter}: to {Unc}", letter, unc);
            var rc = await Task.Run(() =>
                OperatingSystem.IsWindows() ? WindowsDavDrive.Map(ownerWindow, unc, letter) : -1); // guard repeated: CA1416 cannot see through the lambda
            if (rc != 0)
            {
                DesktopLog.Warn("WebDAV mount: WNetAddConnection3 for {Letter}: to {Unc} failed with Win32 error {Rc}", letter, unc, rc);
                return new OpenResult(false, $"Mapping {letter}: failed (Windows error {rc}).");
            }

            DesktopLog.Debug("WebDAV mount: {Letter}: mapped", letter);
        }
        else
        {
            var (fileName, arguments) = BuildOpenCommand(baseUrl, Current);
            var mount = await RunAsync(fileName, arguments, macOsChecksExit: Current == Platform.MacOs);
            if (!mount.Success)
            {
                return mount;
            }

            // Linux mounts AND opens in the one command (xdg-open dav://), so there is nothing left to do.
            if (Current != Platform.MacOs)
            {
                return mount;
            }
        }

        var mountPoint = await WaitForMountAsync(baseUrl);
        if (mountPoint is null)
        {
            DesktopLog.Warn("WebDAV open: the volume for {BaseUrl} did not appear within the wait — run with --verbose and check the mount steps above", baseUrl);
            return new OpenResult(false, $"The volume for {baseUrl} did not appear.");
        }
        DesktopLog.Debug("WebDAV open: volume for {BaseUrl} is at {MountPoint}", baseUrl, mountPoint);

        return await OpenLocalFolderAsync(relative.Length == 0
            ? mountPoint
            : System.IO.Path.Combine(mountPoint, System.IO.Path.Combine(relative.Split('/'))));
    }

    // `mount volume` returns before the mount point is registered, so poll briefly rather than open a path that
    // is about to exist. Short and bounded: this runs off the UI thread, and a mount that has not appeared in a
    // few seconds has not appeared.
    private static async Task<string?> WaitForMountAsync(string baseUrl)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (attempt > 0 && attempt % 8 == 0)
            {
                DesktopLog.Debug("WebDAV wait: the volume for {BaseUrl} has not appeared after {Attempts} probes", baseUrl, attempt);
            }

            if (MountedPathFor(baseUrl) is { } found)
            {
                return found;
            }

            await Task.Delay(250);
        }

        return null;
    }

    // Runs the mount/open command on a background thread and reports success/failure. On macOS a non-zero
    // osascript exit (server unreachable / auth cancelled) is surfaced; explorer.exe/cmd/xdg-open just hand off.
    private static Task<OpenResult> RunAsync(string fileName, string[] arguments, bool macOsChecksExit)
    {
        // Never a credential here: every command this runner sees addresses the share; authentication happens
        // in the OS's own prompt (osascript / WNetAddConnection3 / GVFS), so the full line is safe to log.
        DesktopLog.Debug("WebDAV run: {FileName} {Arguments}", fileName, string.Join(" ", arguments));
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
                DesktopLog.Debug("WebDAV run: {FileName} exited {ExitCode}{Stderr}", fileName, process.ExitCode,
                    string.IsNullOrWhiteSpace(stderr) ? "" : $" — {stderr.Trim()}");
                if (macOsChecksExit && process.ExitCode != 0)
                {
                    return new OpenResult(false, string.IsNullOrWhiteSpace(stderr) ? $"Mount failed (exit {process.ExitCode})." : stderr.Trim());
                }

                // Explorer and xdg-open exit non-zero even on success, so their exit code cannot FAIL the call;
                // it is still worth having in the log, because "handed off, exit 1" beside a wrong window on
                // screen is the whole diagnosis of an open that silently went elsewhere (#820).
                return new OpenResult(true, null);
            }
            catch (Exception e)
            {
                DesktopLog.Warn(e, "WebDAV run: {FileName} could not be started", fileName);
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
    // /Volumes/<name>, where <name> is the LAST path segment of the WebDAV URL — macOS names the mounted volume
    // after it, which is exactly why the single resource is served at /SimplArchive (ADR 0509).
    private static string MacVolumePath(Uri baseUri) => $"/Volumes/{baseUri.AbsolutePath.Trim('/').Split('/')[^1]}";

    public static (string FileName, string[] Arguments) BuildOpenWebDavFileCommand(string httpBaseUrl, string relativePath, Platform platform)
    {
        var baseUri = new Uri(httpBaseUrl.TrimEnd('/'));
        var rel = relativePath.Trim('/');
        switch (platform)
        {
            case Platform.MacOs:
                var posixPath = $"{MacVolumePath(baseUri)}/{rel}";
                return ("osascript", new[]
                {
                    "-e",
                    // Same -2753 trap as BuildOpenCommand: `mount volume` returns nothing, so its result must
                    // not be captured. Nothing here needed it — it was captured only out of symmetry.
                    $"mount volume \"{httpBaseUrl.TrimEnd('/')}\"\ndo shell script \"open \" & quoted form of \"{posixPath}\"",
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
    public static async Task<OpenResult> OpenWebDavFileAsync(string httpBaseUrl, string relativePath)
    {
        // macOS: same reasoning as OpenWebDavFolderAsync — mount, wait, then open the path the OS actually
        // chose. Deriving it from the URL opens the WRONG SERVER'S file when two SimplArchives are mounted.
        if (Current == Platform.MacOs)
        {
            var baseUrl = httpBaseUrl.TrimEnd('/');
            if (MountedPathFor(baseUrl) is null)
            {
                var mount = await RunAsync("osascript", ["-e", $"mount volume \"{baseUrl}\""], macOsChecksExit: true);
                if (!mount.Success)
                {
                    return mount;
                }
            }

            if (await WaitForMountAsync(baseUrl) is not { } mountPoint)
            {
                return new OpenResult(false, $"The volume for {baseUrl} did not appear.");
            }

            var relative = relativePath.Trim('/');
            return await OpenLocalFolderAsync(System.IO.Path.Combine(mountPoint, System.IO.Path.Combine(relative.Split('/'))));
        }

        var (fileName, arguments) = BuildOpenWebDavFileCommand(httpBaseUrl, relativePath, Current);
        return await RunAsync(fileName, arguments, macOsChecksExit: false);
    }

    // The (executable, argument list) that opens an ALREADY-MOUNTED local folder in the file manager. Pure
    // (unit-tested); the run is OpenLocalFolderAsync.
    //
    // Distinct from the WebDAV commands above because there is nothing left to mount: `MountedPath()` has already
    // said the volume is there, so re-issuing `mount volume` would ask the OS to redo work it has done — and on
    // macOS that is what makes the difference between Finder coming forward and a spinner while the mount is
    // re-negotiated. This is the command behind the "already mounted → go straight to the folder" branch.
    public static (string FileName, string[] Arguments) BuildOpenLocalFolderCommand(string path, Platform platform) =>
        platform switch
        {
            Platform.MacOs => ("open", new[] { path }),
            Platform.Windows => ("explorer.exe", new[] { path.Replace('/', '\\') }),
            _ => ("xdg-open", new[] { path }),
        };

    /// <summary>Opens a folder that is already on the filesystem (a mounted volume, or a path inside one).</summary>
    public static Task<OpenResult> OpenLocalFolderAsync(string path)
    {
        // explorer.exe answers a path it cannot open by showing the DOCUMENTS folder — no error, the wrong
        // window (#820). Refuse here instead: a real answer the status line can show beats Explorer's shrug.
        if (Current == Platform.Windows && !System.IO.Directory.Exists(path))
        {
            DesktopLog.Warn("Open folder: {Path} does not exist — refusing rather than letting Explorer fall back to Documents", path);
            return Task.FromResult(new OpenResult(false, $"{path} does not exist."));
        }

        DesktopLog.Debug("Open folder: {Path}", path);
        var (fileName, arguments) = BuildOpenLocalFolderCommand(path, Current);

        // `open` reports a missing path with a non-zero exit, which is worth surfacing: the deep-link folder can
        // legitimately not exist yet (an empty Intray creates no directory), and silently doing nothing is the
        // failure mode this whole button exists to remove.
        return RunAsync(fileName, arguments, macOsChecksExit: Current == Platform.MacOs);
    }

    // https://host:443/SimplArchive/Intray → davs://host:443/SimplArchive/Intray ; http → dav.

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
    /// <summary>Where THIS server's WebDAV URL is mounted, or <c>null</c>.</summary>
    /// <remarks>
    /// <para>
    /// Asks the OS which mount point belongs to this URL rather than deriving one from the URL's last path
    /// segment, because that derivation is wrong the moment a second SimplArchive is mounted. macOS suffixes a
    /// colliding volume name, so two servers both served at <c>/SimplArchive</c> become
    /// <c>/Volumes/SimplArchive</c> and <c>/Volumes/SimplArchive-1</c> — and which one got the bare name is
    /// simply whichever mounted first.
    /// </para>
    /// <para>
    /// The failure that matters there is not the one you see. Deriving the name made an app connected to server
    /// B find server A's mount and deep-link into ITS files: no error, no warning, the wrong archive. Matching on
    /// the URL cannot do that — a mount point either belongs to this server or is not returned.
    /// </para>
    /// </remarks>
    public static string? MountedPathFor(string httpBaseUrl)
    {
        var wanted = httpBaseUrl.TrimEnd('/');
        return Current switch
        {
            // `mount` prints "<source> on <point> (type, …)"; the source is the mounted URL.
            Platform.MacOs => MountEntries()
                .FirstOrDefault(e => string.Equals(e.Source.TrimEnd('/'), wanted, StringComparison.OrdinalIgnoreCase)).Point,
            Platform.Windows => Uri.TryCreate(wanted, UriKind.Absolute, out var server) ? WindowsMountedDrive(server) : null,
            _ => GvfsDavMount(),
        };
    }

    // The (source, point) pairs `mount` reports. Best-effort: if it can't be run, callers fall back to treating
    // the volume as not mounted, which costs a redundant mount attempt rather than opening the wrong thing.
    private static IEnumerable<(string Source, string Point)> MountEntries()
    {
        string output;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/sbin/mount")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                yield break;
            }

            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
        catch (Exception e)
        {
            DesktopLog.Debug("WebDAV probe: running /sbin/mount failed ({Message}) — treating the volume as not mounted", e.Message);
            yield break;
        }

        foreach (var entry in ParseMountOutput(output))
        {
            yield return entry;
        }
    }

    // Pure (unit-tested) so the parsing is pinned without running `mount`.
    public static IEnumerable<(string Source, string Point)> ParseMountOutput(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<source> on <point> (type, …)" — the point ends at the space before the parenthesised options,
            // and a mount point may legitimately contain spaces ("/Volumes/My Disk").
            var on = line.IndexOf(" on ", StringComparison.Ordinal);
            var options = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (on > 0 && options > on)
            {
                yield return (line[..on], line[(on + 4)..options]);
            }
        }
    }

    /// <summary>
    /// Where THIS CLIENT'S server is mounted, or <c>null</c> — matched by host, not by volume name.
    /// </summary>
    /// <remarks>
    /// The name test this replaced (<c>Directory.Exists("/Volumes/SimplArchive")</c>) answers the wrong
    /// question. Every SimplArchive is served at <c>/SimplArchive</c> (ADR 0509), so every one of them wants
    /// that volume name and macOS gives the bare name to whichever mounted first, suffixing the rest. A client
    /// connected to server B therefore saw server A's mount, concluded "already mounted", and deep-linked into
    /// A'S FILES — no error, no warning, the wrong archive. Matching the mount's SOURCE host against the server
    /// this client is talking to cannot confuse two servers, whatever the volumes ended up being called.
    /// </remarks>
    public static string? MountedPath() => MountedPathForHost(DesktopClientOptions.ApiBaseUrl);

    /// <summary>The mount whose source is served by the same host:port as <paramref name="serverUrl"/>.</summary>
    public static string? MountedPathForHost(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
        {
            return null;
        }

        return Current switch
        {
            Platform.MacOs => LogProbe(MountEntries()
                .Where(e => e.Point.StartsWith("/Volumes/", StringComparison.Ordinal))
                .Where(e => Uri.TryCreate(e.Source, UriKind.Absolute, out var src)
                            && string.Equals(src.Host, server.Host, StringComparison.OrdinalIgnoreCase)
                            && src.Port == server.Port)
                .Select(e => e.Point)
                // `mount` keeps listing a WebDAV volume whose server has gone away, and opening one of those
                // hands the user a Finder window that hangs. "Mounted" has to mean reachable, not merely listed.
                .FirstOrDefault(System.IO.Directory.Exists), server),
            Platform.Windows => WindowsMountedDrive(server),
            _ => LogProbe(GvfsDavMount(), server),
        };
    }

    // One Debug line for every "is it mounted?" answer, whatever platform produced it: the probe drives what
    // the WebDAV button DOES next, so a wrong answer here is the first thing to check in a --verbose run.
    private static string? LogProbe(string? result, Uri server)
    {
        DesktopLog.Debug("WebDAV probe: server {Server} -> {Result}", server.Host, result ?? "(not mounted)");
        return result;
    }

    // Which mapped network drive points at THIS server — matched by the remote UNC's host, the same host rule
    // the macOS arm follows (#820). The volume-label test this replaces asked the wrong question: a WebDAV
    // mapping commonly reports NO label, so the client concluded "not mounted" forever and re-opened the bare
    // UNC on every click.
    private static string? WindowsMountedDrive(Uri server)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            if (drive.DriveType != System.IO.DriveType.Network)
            {
                continue;
            }

            var remote = WindowsDavDrive.RemoteOf(char.ToUpperInvariant(drive.Name[0]));
            DesktopLog.Debug("WebDAV probe: {Drive} -> {Remote}", drive.Name, remote ?? "(not a mapping)");
            if (remote is not null && UncMatchesServer(remote, server))
            {
                return drive.Name;
            }
        }

        return null;
    }

    /// <summary>Does a mapped drive's remote UNC (<c>\host@SSL@443\DavWWWRoot\…</c>) point at this server?
    /// Pure (unit-tested): host case-insensitively, port with the scheme's default filled in.</summary>
    public static bool UncMatchesServer(string remoteUnc, Uri server)
    {
        var trimmed = remoteUnc.TrimStart('\\');
        var hostPart = trimmed.Split('\\')[0];
        var pieces = hostPart.Split('@');
        var host = pieces[0];
        var ssl = pieces.Any(p => p.Equals("SSL", StringComparison.OrdinalIgnoreCase));
        var port = pieces.Skip(1).Select(p => int.TryParse(p, out var n) ? (int?)n : null).FirstOrDefault(n => n is not null)
            ?? (ssl ? 443 : 80);
        var serverPort = server.IsDefaultPort ? (server.Scheme == "https" ? 443 : 80) : server.Port;
        return string.Equals(host, server.Host, StringComparison.OrdinalIgnoreCase) && port == serverPort;
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

    private static string ToDavScheme(Uri uri) =>
        (uri.Scheme == "https" ? "davs://" : "dav://") + uri.Authority + uri.AbsolutePath;

    // https://host:443/SimplArchive/Intray → \\host@SSL@443\DavWWWRoot\SimplArchive\Intray ; http → \\host@80\...
    private static string ToWindowsUnc(Uri uri)
    {
        var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
        var host = uri.Scheme == "https" ? $"{uri.Host}@SSL@{port}" : $"{uri.Host}@{port}";
        var path = uri.AbsolutePath.Replace('/', '\\');
        return $"\\\\{host}\\DavWWWRoot{path}";
    }
}
