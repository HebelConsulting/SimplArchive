namespace SimplArchive.ManualCapture;

// The catalog of screens the manual illustrates — one source of truth for the stable file names (desktop-<name>.png
// / web-<name>.png) that the Typst sources reference. Adding/removing a screen here + in the .typ figure is the
// anti-staleness contract: a renamed tab breaks the capture (the figure name has no PNG) and the manual build fails.

// A desktop screen rendered by the Avalonia client's headless --screenshot hooks (Program.cs). `Flags` are the
// extra CLI args appended after `--screenshot <out> --demo`. `Window` selects a dedicated-window hook instead.
// `Pdf` (a repo-relative path) is passed as `--pdf`, so a real PDF renders in the preview pane instead of the
// placeholder text.
public sealed record DesktopScreen(
    string Name,
    string[] Flags,
    DesktopWindow Window = DesktopWindow.Main,
    string? Pdf = null,
    Capture Capture = Capture.Fixture,
    string? WhyFixture = null);

/// <summary>Where a published desktop figure's DATA comes from (#1358).</summary>
public enum Capture
{
    /// <summary>
    /// A hand-written populator in the client's own screenshot hooks. Fast and Docker-free — and unable to
    /// fail when the product moves, which is the whole reason this enum exists. Every one of these must say
    /// WHY in <c>WhyFixture</c>, so a fixture screen can never be mistaken for a live one.
    /// </summary>
    Fixture,

    /// <summary>
    /// Captured against the real seeded app, like every web figure. The figure then cannot describe a dataset
    /// the product does not have.
    /// </summary>
    Live,
}

public enum DesktopWindow
{
    Main,      // --screenshot <out> --demo <flags>
    Logon,     // --logon-screenshot <out>
    Servers,   // --servers-screenshot <out>
    SortDialog, // --sortdialog-screenshot <out> <pdf> — the sort & rotate dialog over the sample batch (#527)
    ContactDialog, // --contact-screenshot <out> --raw — the structured editor with its raw disclosure open (#651)
    BookingDialog, // --booking-screenshot <out> — the Bookings… dialog over a plausible schedule (room reservations)
}

// A web screen: after login, click the bottom tab whose label matches `Tab` (null = the default workbench, or the
// pre-login `Login` page) and screenshot the workbench.
public sealed record WebScreen(string Name, string? Tab, bool BeforeLogin = false);

public static class Screens
{
    // Desktop — the Avalonia fat client. Each maps to an existing demo populator in DesktopClient/Program.cs.
    public static readonly IReadOnlyList<DesktopScreen> Desktop =
    [
        new("logon", [], DesktopWindow.Logon, WhyFixture: "Pre-authentication by definition — there is no session to drive it from."),
        new("workbench", [], Pdf: "src/SimplArchive.Api/DemoData/sample-invoice.pdf", Capture: Capture.Live),
        new("search", ["--search"], WhyFixture: "Needs a query whose hits are stable; live search ordering is the next slice."),
        new("search-hit-overlay", ["--hitoverlay", "--fullscreen"], WhyFixture: "Needs OCR word boxes for a specific page — the heaviest live surface, deliberately last."),
        new("intray", ["--intray"], WhyFixture: "Needs scanned pages staged in the intray, which the seed does not file."),
        new("tasks", ["--workflow", "--tasks"], WhyFixture: "Needs workflow tasks assigned to the captured user; the seed creates none."),
        new("users", ["--users"], WhyFixture: "Live-capturable; not yet migrated (this slice is the workbench only)."),
        new("audit", ["--audit"], WhyFixture: "Needs audit entries whose timestamps would churn the figure on every run."),
        new("recycle-bin", ["--recyclebin"], WhyFixture: "Needs soft-deleted documents, which the seed does not leave behind."),
        new("tenant", ["--tenant"], WhyFixture: "Live-capturable; not yet migrated (this slice is the workbench only)."),
        new("checkout", ["--checkout"], WhyFixture: "Needs a document checked out BY the captured user."),
        new("server-manager", [], DesktopWindow.Servers, WhyFixture: "A pre-session window over local configuration — no app to capture from."),
        // The sort & rotate dialog over the checked-in sample batch — its mis-rotated page 4 shown mid-fix,
        // which is the figure the manual's page-operations section stands on (#527).
        new("sort-rotate", [], DesktopWindow.SortDialog, Pdf: "src/SimplArchive.Api/wwwroot/download/samples/SimplArchive-Patch3-Sample-Batch.pdf", WhyFixture: "A dialog over a fixed sample batch; its mis-rotated page is what the figure is of."),
        new("contacts", ["--contacts"], WhyFixture: "Live-capturable; not yet migrated (this slice is the workbench only)."),
        new("calendar", ["--calendar"], WhyFixture: "Live-capturable; not yet migrated, and dated rows need the frozen clock honoured."),
        // The structured contact editor with the "Advanced: the stored item" disclosure OPEN. Its own window,
        // because neither of the two tabs above can show it — and the disclosure is the part of this feature a
        // reader is least likely to guess at, since its whole point is the properties the form does not show.
        new("contact-editor", [], DesktopWindow.ContactDialog, WhyFixture: "A dialog with its raw disclosure open; no list view can show it."),
        // The Bookings… dialog on a meeting room — reservations above, the new-booking row below, times TYPED.
        // Synthetic fixed-date rows (WindowShots.cs), so the figure does not churn with the calendar.
        new("bookings", [], DesktopWindow.BookingDialog, WhyFixture: "Deliberately synthetic fixed-date rows, so the figure does not churn with the calendar."),
    ];

    // Web — the Blazor workbench. Tab labels match the bottom tab bar (.wb-tab). The demo admin holds every right,
    // so all gated tabs are present.
    public static readonly IReadOnlyList<WebScreen> Web =
    [
        new("login", null, BeforeLogin: true),
        new("repositories", null),
        new("intray", "Intray"),
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
        new("contacts", "Contacts"),
        new("calendar", "Calendar"),
    ];
}
