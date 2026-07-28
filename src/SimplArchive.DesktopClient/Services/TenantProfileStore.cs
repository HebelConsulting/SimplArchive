using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// A configured SimplArchive deployment the desktop client can connect to (ADR "Desktop tenant configuration") —
// a display name + the API-root URL (the base the client talks to; carries any reverse-proxy path prefix, e.g.
// https://host/simplarchive). The user maintains these via the Ctrl/Cmd+P tenant manager.
public sealed class TenantProfile
{
    public string Name { get; set; } = "";
    public string ApiRootUrl { get; set; } = "";
}

// The persisted tenant configuration: the list of deployments + the last-chosen one (remembered across runs).
public sealed class TenantConfig
{
    public List<TenantProfile> Tenants { get; set; } = [];
    public string? LastTenant { get; set; }
    // The last logon choices, restored into the logon window (ADR "Desktop logon window").
    public string? LastUsername { get; set; }
    public string? LastLanguage { get; set; }
}

// Reads/writes the tenant configuration as JSON in the user's app-data directory (mirrors LayoutSettingsStore).
// All IO is best-effort — a missing/unreadable file yields an empty config, and a failed write is swallowed.
public static class TenantProfileStore
{
    private static readonly string FilePath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SimplArchive",
        "tenants.json");

    // Overridable so a test can point at a throwaway file instead of the real app-data path.
    public static string? PathOverride { get; set; }

    private static string Path_ => PathOverride ?? FilePath;

    public static TenantConfig Load()
    {
        try
        {
            return File.Exists(Path_)
                ? JsonSerializer.Deserialize<TenantConfig>(File.ReadAllText(Path_)) ?? new TenantConfig()
                : new TenantConfig();
        }
        catch
        {
            return new TenantConfig();
        }
    }

    public static void Save(TenantConfig config)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Tenant-config persistence is best-effort; ignore IO errors.
        }
    }
}
