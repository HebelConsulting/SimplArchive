using System.Diagnostics;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// Runs the desktop client's headless verification hooks as a subprocess — the one implementation of it, now
/// that a second test needed what <c>DesktopEditProfileTests</c> had privately (the same work in two places is
/// one generic implementation, not two copies).
/// </summary>
/// <remarks>
/// Executes the ALREADY-BUILT client DLL from this test's own output directory — never <c>dotnet run</c>.
/// `dotnet run` was issue #505: it builds first, and when no MSBuild worker nodes exist yet that build SPAWNS
/// them — and they inherit this process's redirected stdout/stderr pipe handles. ReadToEndAsync cannot see EOF
/// while any handle-holder lives, and an idle reuse node exits after exactly 900 seconds — so the suite
/// "stalled" for 15m01s, to the second, and then passed. Executing the DLL avoids the entire class: no build
/// inside the test, no MSBuild, nothing to inherit the pipes — and no rebuild mutating bin/ mid-run either.
/// The DLL, its runtimeconfig and the Avalonia/Skia natives are all here because this project references the
/// client project.
/// </remarks>
public static class DesktopProc
{
    public static async Task<(int ExitCode, string Output)> RunAsync(params string[] appArgs)
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
