using System.Reflection;

namespace SimplArchive.Api;

// The server's own build version, surfaced in the /api discovery document (ADR 0512) so the desktop client can tell
// whether it is behind THIS deployment. Sourced from the Api assembly's AssemblyInformationalVersion — stamped by
// the release build (`-p:Version=<tag>` in the Dockerfile, matching the desktop packaging) — with any `+build`
// metadata stripped. A plain local/dev build (no stamp) reports "1.0.0". Mirrors the desktop's
// Services.ClientUpdate.RunningVersion so the two versions compare on the same shape.
public static class ServerBuildInfo
{
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(ServerBuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var raw = informational
            ?? typeof(ServerBuildInfo).Assembly.GetName().Version?.ToString()
            ?? "1.0.0";
        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
