using System.Diagnostics;

namespace SimplArchive.ManualCapture;

// Captures the desktop (Avalonia) screens by shelling out to the client's own headless --screenshot hooks
// (DesktopClient/Program.cs) — one subprocess per screen. Fully self-contained: the hooks render in-process demo
// data via Avalonia.Headless + Skia, so there is no backend/Docker/Chrome dependency. Cheap enough to run as the PR
// gate (ADR 0502).
public static class DesktopCapture
{
    public static async Task RunAsync(string outDir)
    {
        var repoRoot = Paths.RepoRoot();
        var desktopCsproj = Path.Combine(repoRoot, "src", "SimplArchive.DesktopClient", "SimplArchive.DesktopClient.csproj");

        foreach (var screen in Screens.Desktop)
        {
            var outPath = Path.Combine(outDir, $"desktop-{screen.Name}.png");
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }

            string[] pdfArg = screen.Pdf is { } rel ? ["--pdf", Path.Combine(repoRoot, rel)] : [];
            var appArgs = screen.Window switch
            {
                DesktopWindow.Logon => new[] { "--logon-screenshot", outPath },
                DesktopWindow.Servers => ["--servers-screenshot", outPath],
                _ => ["--screenshot", outPath, "--demo", .. screen.Flags, .. pdfArg],
            };

            Console.WriteLine($"[desktop] {screen.Name} → {Path.GetFileName(outPath)}");
            await RunDesktopAsync(repoRoot, desktopCsproj, appArgs);

            if (!File.Exists(outPath))
            {
                throw new InvalidOperationException(
                    $"Desktop capture '{screen.Name}' produced no file ({outPath}). A screen may have been renamed/removed — update Screens.cs and the desktop --screenshot hooks.");
            }
        }
    }

    private static async Task RunDesktopAsync(string repoRoot, string csproj, string[] appArgs)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "run", "--project", csproj, "--no-build", "--no-launch-profile", "--" })
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

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Desktop client exited {proc.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
    }
}
