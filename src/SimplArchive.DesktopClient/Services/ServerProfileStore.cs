using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// A SimplArchive server the desktop client can connect to (ADR "Desktop server configuration") — a display name
// + the API-root URL (the base the client talks to; carries any reverse-proxy path prefix, e.g.
// https://host/simplarchive). The user maintains these via the Ctrl/Cmd+P server manager.
//
// Deliberately NOT called a tenant. A SimplArchive server is itself multi-tenant: one installation hosts many
// tenants, and which tenant the session belongs to is resolved *after* login, from the account signing in. What
// the user picks here is the installation — a base URL. Calling it a tenant made the word mean two different
// things in one product, and put the wrong one on the first screen a newcomer sees (issue #417).
public sealed class ServerProfile
{
    public string Name { get; set; } = "";
    public string ApiRootUrl { get; set; } = "";

    // The style this server wears (ADR 0578) — an id from ThemeCatalog, or null/"default" for the shipped
    // design. Per PROFILE rather than per application, so connecting to a customer's server shows their
    // colours; an id that no longer resolves falls back to the shipped design without complaint.
    public string? Theme { get; set; }
}

// The persisted server configuration: the list of servers + the last-chosen one (remembered across runs).
public sealed class ServerConfig
{
    public List<ServerProfile> Servers { get; set; } = [];
    public string? LastServer { get; set; }
    // The last logon choices, restored into the logon window (ADR "Desktop logon window").
    public string? LastUsername { get; set; }
    public string? LastLanguage { get; set; }
}

// Reads/writes the server configuration as JSON in the user's app-data directory (mirrors LayoutSettingsStore).
// All IO is best-effort — a missing/unreadable file yields an empty config, and a failed write is swallowed.
public static class ServerProfileStore
{
    private static readonly string FilePath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SimplArchive",
        "servers.json");

    // Overridable so a test can point at a throwaway file instead of the real app-data path.
    public static string? PathOverride { get; set; }

    private static string Path_ => PathOverride ?? FilePath;

    public static ServerConfig Load()
    {
        try
        {
            return File.Exists(Path_)
                ? JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(Path_)) ?? new ServerConfig()
                : new ServerConfig();
        }
        catch
        {
            return new ServerConfig();
        }
    }

    public static void Save(ServerConfig config)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Server-config persistence is best-effort; ignore IO errors.
        }
    }
}
