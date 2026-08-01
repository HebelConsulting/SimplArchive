using SimplArchive.ManualCapture;

// The user-manual screenshot-capture harness (ADR 0502). Emits a deterministic set of PNGs the Typst manual
// references by stable name, so the manual is always in step with the real UI.
//
//   dotnet run --project tests/SimplArchive.ManualCapture -- [--desktop] [--web] [--out <dir>]
//
//   --desktop   capture the Avalonia desktop screens (cheap, self-contained — the PR gate)
//   --web       capture the Blazor web screens (heavy — Testcontainers + Chrome; main only)
//   (neither)   capture both
//   --out <dir> output directory (default: manual/screenshots)

var desktop = args.Contains("--desktop");
var web = args.Contains("--web");
if (!desktop && !web)
{
    desktop = web = true;
}

var outIndex = Array.IndexOf(args, "--out");
var outDir = outIndex >= 0 && outIndex + 1 < args.Length
    ? Path.GetFullPath(args[outIndex + 1])
    : Path.Combine(Paths.RepoRoot(), "manual", "screenshots");
Directory.CreateDirectory(outDir);
Console.WriteLine($"[manual-capture] output → {outDir}");

if (desktop)
{
    await DesktopCapture.RunAsync(outDir);
}

if (web)
{
    await WebCapture.RunAsync(outDir);
}

Console.WriteLine("[manual-capture] done.");
