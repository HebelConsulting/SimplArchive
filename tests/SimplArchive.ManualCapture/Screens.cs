namespace SimplArchive.ManualCapture;

// The catalog of screens the manual illustrates — one source of truth for the stable file names (desktop-<name>.png
// / web-<name>.png) that the Typst sources reference. Adding/removing a screen here + in the .typ figure is the
// anti-staleness contract: a renamed tab breaks the capture (the figure name has no PNG) and the manual build fails.

// A desktop screen rendered by the Avalonia client's headless --screenshot hooks (Program.cs). `Flags` are the
// extra CLI args appended after `--screenshot <out> --demo`. `Window` selects a dedicated-window hook instead.
// `Pdf` (a repo-relative path) is passed as `--pdf`, so a real PDF renders in the preview pane instead of the
// placeholder text.
public sealed record DesktopScreen(string Name, string[] Flags, DesktopWindow Window = DesktopWindow.Main, string? Pdf = null);

public enum DesktopWindow
{
    Main,      // --screenshot <out> --demo <flags>
    Logon,     // --logon-screenshot <out>
    Tenants,   // --tenants-screenshot <out>
}

// A web screen: after login, click the bottom tab whose label matches `Tab` (null = the default workbench, or the
// pre-login `Login` page) and screenshot the workbench.
public sealed record WebScreen(string Name, string? Tab, bool BeforeLogin = false);

public static class Screens
{
    // Desktop — the Avalonia fat client. Each maps to an existing demo populator in DesktopClient/Program.cs.
    public static readonly IReadOnlyList<DesktopScreen> Desktop =
    [
        new("logon", [], DesktopWindow.Logon),
        new("workbench", [], Pdf: "src/SimplArchive.Api/DemoData/sample-invoice.pdf"),
        new("search", ["--search"]),
        new("search-hit-overlay", ["--hitoverlay", "--fullscreen"]),
        new("inbox", ["--inbox"]),
        new("tasks", ["--workflow", "--tasks"]),
        new("users", ["--users"]),
        new("audit", ["--audit"]),
        new("recycle-bin", ["--recyclebin"]),
        new("tenant", ["--tenant"]),
        new("checkout", ["--checkout"]),
        new("tenant-manager", [], DesktopWindow.Tenants),
    ];

    // Web — the Blazor workbench. Tab labels match the bottom tab bar (.wb-tab). The demo admin holds every right,
    // so all gated tabs are present.
    public static readonly IReadOnlyList<WebScreen> Web =
    [
        new("login", null, BeforeLogin: true),
        new("repositories", null),
        new("inbox", "Inbox"),
        new("checkout", "Check-out"),
        new("search", "Search"),
        new("recycle-bin", "Recycle bin"),
        new("tasks", "Tasks"),
        new("my-work", "My work"),
        new("users", "Users & groups"),
        new("audit", "Audit"),
        new("legal-holds", "Legal holds"),
        new("retention", "Retention"),
        new("tenant", "Tenant"),
        new("tags", "Tags"),
    ];
}
