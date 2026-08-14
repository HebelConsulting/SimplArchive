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
        var outPath = Path.Combine(Path.GetTempPath(), $"edit-profile-{Guid.NewGuid():N}.png");

        try
        {
            var (exitCode, output) = await RunDesktopAsync(["--profile-screenshot", outPath]);

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

    // Executes the ALREADY-BUILT client DLL from this test's own output directory — never `dotnet run`.
    //
    // `dotnet run` was issue #505, and the mechanism deserves spelling out because it will read as impossible
    // otherwise: `dotnet run` builds first, and when no MSBuild worker nodes exist yet, that build SPAWNS them —
    // and they inherit this process's redirected stdout/stderr pipe handles. ReadToEndAsync cannot see EOF while
    // any handle-holder lives, and an idle reuse node exits after exactly 900 seconds — so the suite "stalled"
    // for 15m01s, four separate times, to the second, and then passed. Whether it stalled at all depended on
    // whether an EARLIER build's nodes were still alive to be reused (a reused node was spawned by someone else
    // and holds no test pipe), which is why it looked correlated with what ran before the suite: the real
    // trigger was a preceding `dotnet build-server shutdown`, not the E2E suite.
    //
    // Executing the DLL avoids the entire class: no build inside the test, no MSBuild, nothing to inherit the
    // pipes — and no rebuild mutating bin/ mid-run either. The DLL, its runtimeconfig and the Avalonia/Skia
    // natives are all here because this project references the client project.
    private static async Task<(int ExitCode, string Output)> RunDesktopAsync(string[] appArgs)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SimplArchive.DesktopClient.dll"));
        foreach (var a in appArgs)
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the desktop client.");
        // Both pipes drained CONCURRENTLY: sequential ReadToEnd deadlocks if the second pipe's buffer fills
        // while the first is being drained.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdout + await stderr);
    }
}
