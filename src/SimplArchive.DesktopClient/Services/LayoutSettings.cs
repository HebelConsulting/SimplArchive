using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// Persisted workbench pane layout — sizes (as GridLength strings, e.g. "1.4*" or "220") and collapsed state
// for the four collapsible panes. Mirrors the web client's localStorage layout (ADR 0224). See ADR
// "Desktop collapsible/resizable panes".
public sealed class LayoutSettings
{
    public string TreeWidth { get; set; } = "1.4*";
    public string ListWidth { get; set; } = "2*";
    public string IndexHeight { get; set; } = "1.5*";
    public string ChatWidth { get; set; } = "2*";

    public bool TreeCollapsed { get; set; }
    public bool ListCollapsed { get; set; }
    public bool IndexCollapsed { get; set; }
    public bool ChatCollapsed { get; set; }

    // The Inbox tab's four collapsible panes (ADR "Collapsible inbox panes").
    public string InboxServerHeight { get; set; } = "1*";
    public string InboxLocalHeight { get; set; } = "1*";
    public string InboxMaskHeight { get; set; } = "1.1*";
    public string InboxPreviewHeight { get; set; } = "1.6*";

    public bool InboxServerCollapsed { get; set; }
    public bool InboxLocalCollapsed { get; set; }
    public bool InboxMaskCollapsed { get; set; }
    public bool InboxPreviewCollapsed { get; set; }

    // The Repositories contents-list column widths, in pixels (ADR "Desktop list-pane resizable columns"):
    // Name | Type | Doc date | Size | Tags. Persisted so a resized layout survives restart.
    public string ColName { get; set; } = "260";
    public string ColType { get; set; } = "130";
    public string ColDate { get; set; } = "96";
    public string ColSize { get; set; } = "72";
    public string ColTags { get; set; } = "160";
}

// Reads/writes LayoutSettings as JSON in the user's app-data directory. All IO is best-effort — a missing or
// unreadable file just yields defaults, and a failed write is swallowed (layout persistence is non-critical).
public static class LayoutSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SimplArchive",
        "desktop-layout.json");

    public static LayoutSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<LayoutSettings>(File.ReadAllText(FilePath)) ?? new LayoutSettings()
                : new LayoutSettings();
        }
        catch
        {
            return new LayoutSettings();
        }
    }

    public static void Save(LayoutSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Layout persistence is non-critical; ignore IO errors.
        }
    }
}
