using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SimplArchive.DesktopClient.Services;

// Optional integration with Beyond Compare (scootersoftware.com) if the user has it installed (ADR "Document
// version comparison") — a native-client-only capability the web can't offer. We never bundle or depend on it;
// we just launch the user's own installed copy against two version files, like opening a file in any native app.
public static class BeyondCompare
{
    // The launcher executable, or null when Beyond Compare isn't installed. macOS/Linux ship a `bcomp` CLI
    // helper (it blocks until the window closes); on Windows we launch BCompare.exe directly.
    public static string? FindExecutable()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fall back to a PATH lookup for the CLI name.
        var pathName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "BCompare.exe"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "bcomp"
            : "bcompare";
        return FindOnPath(pathName);
    }

    public static bool IsInstalled => FindExecutable() is not null;

    // Launches Beyond Compare comparing the two files. Best-effort — returns false if it couldn't start.
    public static bool Launch(string file1, string file2)
    {
        var exe = FindExecutable();
        if (exe is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, $"\"{file1}\" \"{file2}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Beyond Compare.app/Contents/MacOS/bcomp";
            yield return "/usr/local/bin/bcomp";
            yield return "/opt/homebrew/bin/bcomp";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var version in new[] { "Beyond Compare 5", "Beyond Compare 4", "Beyond Compare 3" })
            {
                yield return Path.Combine(pf, version, "BCompare.exe");
            }
        }
        else
        {
            yield return "/usr/bin/bcompare";
            yield return "/usr/local/bin/bcompare";
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir, fileName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception)
            {
                // an invalid PATH entry — skip
            }
        }

        return null;
    }
}
