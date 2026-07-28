using System;
using System.Diagnostics;
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
        var isMacOs = Current == Platform.MacOs;
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
                if (isMacOs && process.ExitCode != 0)
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

    // https://host:443/webdav/Inbox → davs://host:443/webdav/Inbox ; http → dav.
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
