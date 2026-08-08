using System.Reflection;

namespace SimplArchive.Api;

// The server's own build version, surfaced in the /api discovery document (ADR 0512) so the desktop client can tell
// whether it is behind THIS deployment. Sourced from the Api assembly's AssemblyInformationalVersion — stamped by
// the release build (`-p:Version=<tag>` in the Dockerfile, matching the desktop packaging) — with any `+build`
// metadata stripped. A plain local/dev build reports the "0.0.0-dev" sentinel (issue #425). Mirrors the desktop's
// Services.ClientUpdate.RunningVersion so the two versions compare on the same shape.
public static class ServerBuildInfo
{
    /// <summary>
    /// What an unstamped build reports. Sorts below every real release, so it can never claim to be ahead of a
    /// client, and is unmistakably not a version anyone released (issue #425).
    /// </summary>
    public const string UnstampedVersion = "0.0.0-dev";

    public static string Version { get; } = Resolve();

    /// <summary>
    /// False when the build carries no release version — the deployment cannot say which build it is.
    /// </summary>
    public static bool IsStamped => !string.Equals(Version, UnstampedVersion, StringComparison.Ordinal);

    private static string Resolve()
    {
        var informational = typeof(ServerBuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var raw = informational
            ?? typeof(ServerBuildInfo).Assembly.GetName().Version?.ToString()
            ?? UnstampedVersion;
        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
