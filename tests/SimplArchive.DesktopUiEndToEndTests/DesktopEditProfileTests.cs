using System.Diagnostics;

namespace SimplArchive.UiEndToEndTests;

// The "Edit profile" window renders (#464), driven through the desktop client's own headless screenshot hook.
//
// Why a rendering check at all: this is the one surface where the photo crop is hosted INLINE rather than as its
// own dialog, so the window composes a UserControl that was extracted from a Window in the same change. A
// mistake there — an unresolved control, a missing resource, a broken binding — throws at CONSTRUCTION, which no
// api-client or view-model test in this suite would see, and which nothing else exercises because a Window
// cannot appear in a full-app screenshot.
//
// It shells out rather than building Avalonia headlessly in-process: this suite is deliberately display-free and
// VM-level, and the desktop client already owns that hook (CLAUDE.md lists it among the headless verification
// flags). Reusing it keeps one implementation of "render this window without a display".
public class DesktopEditProfileTests
{
    [Fact]
    public async Task The_edit_profile_window_renders_headlessly()
    {
        var repoRoot = RepoRoot();
        var outPath = Path.Combine(Path.GetTempPath(), $"edit-profile-{Guid.NewGuid():N}.png");

        try
        {
            var (exitCode, output) = await RunDesktopAsync(repoRoot, ["--profile-screenshot", outPath]);

            Assert.True(exitCode == 0, $"The desktop client exited {exitCode}:\n{output}");
            Assert.True(File.Exists(outPath), $"No screenshot was produced at {outPath}.\n{output}");

            // A blank or near-blank frame means the window built but laid out nothing — which is what a broken
            // inline host looks like, and it would pass a mere "file exists" check.
            Assert.True(new FileInfo(outPath).Length > 4096,
                $"The screenshot is {new FileInfo(outPath).Length} bytes — the window rendered essentially nothing.");
        }
        finally
        {
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDesktopAsync(string repoRoot, string[] appArgs)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var a in new[] { "run", "--project", "src/SimplArchive.DesktopClient", "--no-launch-profile", "--" })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in appArgs)
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the desktop client.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout + stderr);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
