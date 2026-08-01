using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

// The outcome of comparing the running client against the build a server offers (issue #271).
public enum ClientUpdateKind
{
    // The offered build is the same as (or older than) the running one — nothing to do.
    UpToDate,
    // The offered build is a strictly-newer semver — an update is available.
    UpdateAvailable,
    // One side is a git short-SHA (an untagged dev build, not semver) so a version order can't be established;
    // the offered build merely differs from the running one.
    Inconclusive,
}

// A server-offered client build for comparison against the running one.
public sealed record UpdateInfo(string OfferedVersion, string? DownloadUrl, ClientUpdateKind Kind);

// Self-update check (issue #271): reads the server's browsable download area (ADR "Browsable desktop-client
// download area") for the current OS, parses the offered build's version out of the artifact name
// (`SimplArchive-<version>-<rid>.<ext>`), and compares it with the running client's stamped version. Best-effort:
// an unreachable / unparseable listing yields null (no update surface shown).
public static partial class ClientUpdate
{
    // The download-area subfolder for the running OS (ADR "Browsable desktop-client download area":
    // /download/clients/{windows|linux|macos}/).
    public static string CurrentOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "linux";

    // The running client's version, stamped by the packaging (`-p:Version`, ADR "Windows + Linux desktop
    // packaging"): the informational version, with any build metadata (`+sha`) stripped. A plain `dotnet run`
    // dev build defaults to "1.0.0".
    public static string RunningVersion
    {
        get
        {
            var informational = typeof(DesktopClientOptions).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var raw = informational
                ?? typeof(DesktopClientOptions).Assembly.GetName().Version?.ToString()
                ?? "1.0.0";
            var plus = raw.IndexOf('+');
            return plus >= 0 ? raw[..plus] : raw;
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        await CheckAsync(baseUrl, RunningVersion, cancellationToken);

    // Overload taking an explicit running version so the comparison is unit-testable without the assembly stamp.
    public static async Task<UpdateInfo?> CheckAsync(string baseUrl, string runningVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var listingUrl = $"{baseUrl.TrimEnd('/')}/download/clients/{CurrentOs}/";
            using var resp = await http.GetAsync(listingUrl, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await resp.Content.ReadAsStringAsync(cancellationToken);
            var offered = ParseOfferedBuild(html);
            if (offered is null)
            {
                return null;
            }

            var kind = Compare(runningVersion, offered.Value.Version);
            var downloadUrl = $"{baseUrl.TrimEnd('/')}/download/clients/{CurrentOs}/{offered.Value.FileName}";
            return new UpdateInfo(offered.Value.Version, downloadUrl, kind);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Extracts the offered build (its version + the artifact file name) from a download-area directory listing.
    // Matches every `SimplArchive-<version>-<rid>.<ext>` artifact and picks the highest semver (or, if none parse
    // as semver, the first) — a listing normally holds one build, but macOS ships arm64 + x64 of the same version.
    internal static (string Version, string FileName)? ParseOfferedBuild(string html)
    {
        var matches = ArtifactRegex().Matches(html);
        var builds = new List<(string Version, string FileName)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var fileName = m.Value;
            if (seen.Add(fileName))
            {
                builds.Add((m.Groups["ver"].Value, fileName));
            }
        }

        if (builds.Count == 0)
        {
            return null;
        }

        var semver = builds
            .Where(b => TryParseSemver(b.Version, out _))
            .OrderByDescending(b => { TryParseSemver(b.Version, out var v); return v; })
            .ToList();

        return semver.Count > 0 ? semver[0] : builds[0];
    }

    // Orders the running vs offered version. Equal strings → up to date; both parse as semver → numeric order;
    // otherwise (a git short-SHA on either side) → inconclusive (issue #271: "don't install until advised").
    public static ClientUpdateKind Compare(string running, string offered)
    {
        if (string.Equals(running, offered, StringComparison.OrdinalIgnoreCase))
        {
            return ClientUpdateKind.UpToDate;
        }

        if (TryParseSemver(running, out var r) && TryParseSemver(offered, out var o))
        {
            return o > r ? ClientUpdateKind.UpdateAvailable : ClientUpdateKind.UpToDate;
        }

        return ClientUpdateKind.Inconclusive;
    }

    // A version token is semver-comparable if, after stripping a leading "v" and any "-pre"/"+build" suffix, it
    // parses as a dotted numeric System.Version. A git short-SHA (e.g. "a1b2c3d") won't parse.
    internal static bool TryParseSemver(string value, out Version version)
    {
        version = new Version(0, 0);
        var s = value.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            s = s[..cut];
        }

        return s.Contains('.') && Version.TryParse(s, out version!);
    }

    // Artifact names the packaging produces (ADR "Windows + Linux desktop packaging" / macOS .dmg): the version
    // is everything between "SimplArchive-" and the runtime-id suffix.
    [GeneratedRegex(@"SimplArchive-(?<ver>[^/\s""<>]+?)-(?:win-x64\.zip|linux-x64\.tar\.gz|arm64\.dmg|x64\.dmg)")]
    private static partial Regex ArtifactRegex();
}
