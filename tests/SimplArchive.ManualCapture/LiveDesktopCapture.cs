using System.Diagnostics;
using System.Security.Cryptography;
using SimplArchive.SelfHosting;

namespace SimplArchive.ManualCapture;

// Captures the desktop screens classified Capture.Live against the REAL seeded app (#1358) — the same
// SelfHostedApp engine the web capture already boots, so on `main` this is incremental cost rather than new
// cost: Docker is up for the web shots anyway. Chrome is NOT needed; the desktop client renders headlessly.
//
// WHY THIS EXISTS. The published desktop figures came from a hand-written fixture while the web figures were
// driven live, so one manual described two datasets — and a fixture cannot fail when the product moves, which
// makes the drift silent by construction. The fixture path stays in DesktopCapture as the PR gate's
// Docker-free render smoke; this is what the published manual uses.
public static class LiveDesktopCapture
{
    public static async Task RunAsync(string outDir)
    {
        var live = Screens.Desktop.Where(s => s.Capture == Capture.Live).ToList();
        if (live.Count == 0)
        {
            Console.WriteLine("[desktop-live] no screens are classified Capture.Live — nothing to do.");
            return;
        }

        var repoRoot = Paths.RepoRoot();
        var desktopCsproj = Path.Combine(repoRoot, "src", "SimplArchive.DesktopClient", "SimplArchive.DesktopClient.csproj");

        // The same frozen clock the web capture uses (ADR 0510). Byte-determinism is the property that makes a
        // regenerated manual's diff readable, and two capture paths against two clocks could not share a manual.
        await using var app = new SelfHostedApp { DemoClock = "2026-06-01T09:00:00Z", WithOcrSidecar = true };
        await app.StartAsync();
        Console.WriteLine($"[desktop-live] app ready at {app.BaseUrl}");

        var token = await SelfHostedLogin.GetUserTokenAsync(app.BaseUrl);

        foreach (var screen in live)
        {
            var outPath = Path.Combine(outDir, $"desktop-{screen.Name}.png");
            Console.WriteLine($"[desktop-live] {screen.Name} → {Path.GetFileName(outPath)}");

            // TWICE, and the second one into a scratch file: a live capture has ordering and timing surfaces a
            // fixture does not, and a figure that differs run to run makes every regenerated-manual commit
            // unreadable — so nondeterminism FAILS here rather than churning on main forever (owner-decided).
            await CaptureAsync(repoRoot, desktopCsproj, outPath, app.BaseUrl, token, screen.Name);
            var second = Path.Combine(Path.GetTempPath(), $"manual-determinism-{screen.Name}-{Guid.NewGuid():N}.png");
            try
            {
                await CaptureAsync(repoRoot, desktopCsproj, second, app.BaseUrl, token, screen.Name);
                if (!Same(outPath, second))
                {
                    throw new InvalidOperationException(
                        $"The live capture of '{screen.Name}' is NOT deterministic: two runs against the same "
                        + "app produced different bytes. Find what moves — an ordering that is not pinned, a "
                        + "relative timestamp, a value the frozen clock does not reach — rather than accepting "
                        + "a figure that changes on every main run. Until it is fixed, classify the screen "
                        + "Capture.Fixture with that as its reason.");
                }
            }
            finally
            {
                File.Delete(second);
            }
        }
    }

    private static bool Same(string a, string b)
    {
        using var left = File.OpenRead(a);
        using var right = File.OpenRead(b);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }

    private static async Task CaptureAsync(
        string repoRoot, string csproj, string outPath, string baseUrl, string token, string name)
    {
        if (File.Exists(outPath))
        {
            File.Delete(outPath);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // --framework is required since the desktop client multi-targeted (ADR 0831); net10.0 is always right
        // here, because the capture harness runs on Linux and macOS.
        foreach (var a in new[]
                 {
                     "run", "--project", csproj, "--framework", "net10.0", "--no-build", "--no-launch-profile",
                     "--", "--live-screenshot", outPath, baseUrl, token,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the desktop client.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Live capture '{name}' exited {proc.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        // A capture that writes nothing and says nothing is how a stale figure ships — the same lesson the
        // fixture path records.
        if (!File.Exists(outPath))
        {
            throw new InvalidOperationException(
                $"Live capture '{name}' produced no file ({outPath}), though the client exited 0.\nSTDOUT:\n{stdout}");
        }
    }
}
