using System;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

// The outcome of comparing the running client against the server's version (issue #312).
public enum ClientUpdateKind
{
    // The running client is the same as (or newer than) the server — nothing to do.
    UpToDate,
    // The running client is strictly older than the server AND a matching client release exists — update available.
    UpdateAvailable,
    // One side is a git short-SHA (an untagged dev build, not semver) so a version order can't be established.
    Inconclusive,
}

// A client build offered for upgrade (its version + the download URL of the OS/arch-matching asset).
public sealed record UpdateInfo(string OfferedVersion, string? DownloadUrl, ClientUpdateKind Kind);

// Self-update check (issue #312, ADR 0512). Supersedes the original download-folder scan (ADR 0499): the browsable
// /download area reflects whatever a server happens to host, whereas the GitHub Releases page is the authoritative
// source of published, versioned client artifacts. The check now:
//   1. reads the SERVER's own build version from its `/api` discovery document (ADR 0512),
//   2. proceeds only when the running client is strictly OLDER than the server,
//   3. looks up the GitHub Release tagged exactly `v<serverVersion>` on the public mirror, and
//   4. resolves the asset matching this client's OS + CPU architecture.
// It surfaces a notice ONLY when the client is behind the server AND a matching client release actually exists —
// so the user isn't nagged when no suitable upgrade is published yet. Best-effort: any unreachable / unparseable /
// rate-limited step yields null (no update surface shown).
public static class ClientUpdate
{
    // The public mirror hosts the versioned client releases + assets (ADR 0512); the private canonical publishes none.
    public const string ReleasesRepo = "HebelConsulting/SimplArchive";
    private const string GitHubApiBase = "https://api.github.com";

    // The running OS, for asset selection.
    public static string CurrentOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "linux";

    // The release-asset filename suffix for this client's OS + CPU architecture, matching the packaging's artifact
    // names (ADR 0478 win/linux, ADR 0444 macOS): windows → win-x64.zip; linux → linux-x64.tar.gz; macOS on Apple
    // Silicon → arm64.dmg, on Intel → x64.dmg.
    public static string AssetSuffix => CurrentOs switch
    {
        "windows" => "win-x64.zip",
        "macos" => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64.dmg" : "x64.dmg",
        _ => "linux-x64.tar.gz",
    };

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
        await CheckAsync(baseUrl, RunningVersion, AssetSuffix, cancellationToken);

    // Overload taking an explicit running version + asset suffix so the flow is drivable without the assembly stamp
    // or the host's real OS/arch.
    public static async Task<UpdateInfo?> CheckAsync(string baseUrl, string runningVersion, string assetSuffix, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            // GitHub's API rejects requests without a User-Agent (403); harmless on the /api call too.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SimplArchive-DesktopClient");

            // (1) The server's own version, from its /api discovery document.
            var apiDoc = await http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api", cancellationToken);
            var serverVersion = ParseServerVersion(apiDoc);
            if (serverVersion is null)
            {
                return null;
            }

            // (2) Only proceed when the running client is strictly older than the server. Otherwise there's nothing
            //     to offer — surface the (non-actionable) kind so the caller can stay silent / show a dev-build note.
            var kind = Compare(runningVersion, serverVersion);
            if (kind != ClientUpdateKind.UpdateAvailable)
            {
                return new UpdateInfo(serverVersion, null, kind);
            }

            // (3) The GitHub release tagged exactly v<serverVersion> on the public mirror. A 404 (no release yet) or
            //     403 (rate-limited) → no matching upgrade → stay silent.
            using var resp = await http.GetAsync($"{GitHubApiBase}/repos/{ReleasesRepo}/releases/tags/v{serverVersion}", cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var releaseJson = await resp.Content.ReadAsStringAsync(cancellationToken);

            // (4) The asset matching this OS + architecture. No matching asset → no notice (issue #312: don't nag
            //     when no suitable client for this platform is published).
            var downloadUrl = PickAsset(releaseJson, assetSuffix);
            return downloadUrl is null
                ? null
                : new UpdateInfo(serverVersion, downloadUrl, ClientUpdateKind.UpdateAvailable);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Extracts the server's build version from its `/api` discovery document (the `serverVersion` field, ADR 0512).
    internal static string? ParseServerVersion(string apiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(apiJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("serverVersion", out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. an HTML error page) — treat as undiscoverable.
        }

        return null;
    }

    // Finds the browser_download_url of the release asset whose name ends with the given OS+arch suffix — e.g.
    // "SimplArchive-0.1.1-win-x64.zip" for the suffix "win-x64.zip". Null if the release carries no matching asset.
    internal static string? PickAsset(string releaseJson, string assetSuffix)
    {
        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is not null
                    && name.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var url)
                    && url.ValueKind == JsonValueKind.String)
                {
                    return url.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed release payload — no offer.
        }

        return null;
    }

    // Orders the running client vs the server version. Equal strings → up to date; both parse as semver → numeric
    // order (server strictly newer → an update may be available); otherwise (a git short-SHA on either side) →
    // inconclusive.
    public static ClientUpdateKind Compare(string running, string server)
    {
        if (string.Equals(running, server, StringComparison.OrdinalIgnoreCase))
        {
            return ClientUpdateKind.UpToDate;
        }

        if (TryParseSemver(running, out var r) && TryParseSemver(server, out var s))
        {
            return s > r ? ClientUpdateKind.UpdateAvailable : ClientUpdateKind.UpToDate;
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
}
