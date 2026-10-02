using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Every desktop figure says where its DATA comes from (#1358).
//
// The published desktop screens came from a hand-written fixture while the web ones were driven live, so one
// manual described two datasets — and a fixture cannot fail when the product moves, so the drift was silent by
// construction. Measured when the first screen was migrated, the published workbench figure was wrong about the
// tree, the folder contents, the breadcrumb, the workflow status, the index fields and the chat thread — while
// its document NAMES had been guarded since #1357, which is the whole point: names were the cheap half.
//
// This cannot check that a figure is RIGHT. Nothing automatic can; that is what looking at the render is for.
// What it can do is make the distinction impossible to lose: a screen is Live, or it says why it is not.
//
// READS THE FILE rather than referencing the harness, deliberately. SimplArchive.ManualCapture pulls in the
// self-hosting engine and Testcontainers, and this project is the one the mirror-publish gate now builds and
// runs on every publish (#1384) — a structural question about a literal list is not worth that dependency.
// The floor assertions below are what keep a text scan honest.
public class ManualCaptureClassificationTests
{
    private static readonly string Source =
        File.ReadAllText(Path.Combine(RepoPaths.RootOrNull()!, "tests", "SimplArchive.ManualCapture", "Screens.cs"));

    // The desktop list only: the web screens are all live by construction (there is no fixture path for them).
    private static IReadOnlyList<(string Name, string Entry)> DesktopEntries()
    {
        var desktop = Source[Source.IndexOf("IReadOnlyList<DesktopScreen> Desktop", StringComparison.Ordinal)..];
        desktop = desktop[..desktop.IndexOf("];", StringComparison.Ordinal)];

        return Regex.Matches(desktop, @"new\(""(?<name>[a-z0-9-]+)""(?<rest>[^\n]*)")
            .Select(m => (m.Groups["name"].Value, m.Groups["rest"].Value))
            .ToList();
    }

    [Fact]
    public void Every_desktop_screen_declares_where_its_data_comes_from()
    {
        var undeclared = DesktopEntries()
            .Where(e => !e.Entry.Contains("Capture.Live") && !e.Entry.Contains("WhyFixture:"))
            .Select(e => e.Name)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "These desktop screens are captured from the FIXTURE and do not say why: "
            + string.Join(", ", undeclared) + ". A fixture screen is not a defect — several are genuinely "
            + "un-live-capturable (a pre-authentication window, a dialog over a fixed sample batch) — but one "
            + "that does not SAY so is indistinguishable from a screen nobody got round to, which is the "
            + "distinction this exists to keep. Add the reason, or classify it Capture.Live.");
    }

    [Fact]
    public void A_live_screen_carries_no_fixture_excuse()
    {
        // Contradicting fields are how a stale reason outlives what it explained: a screen migrated to live with
        // its old "not yet migrated" note still attached reads as un-migrated to whoever picks up the next slice.
        var contradictory = DesktopEntries()
            .Where(e => e.Entry.Contains("Capture.Live") && e.Entry.Contains("WhyFixture:"))
            .Select(e => e.Name)
            .ToList();

        Assert.True(contradictory.Count == 0,
            "Captured LIVE but still carrying a WhyFixture reason: " + string.Join(", ", contradictory)
            + ". Drop the reason when the screen migrates.");
    }

    [Fact]
    public void The_scan_sees_the_real_list_and_at_least_one_live_screen()
    {
        // ANTI-VACUOUS, and this is the assertion that makes a text scan trustworthy. A regex that silently
        // stops matching — a reformatted list, a renamed record — would make every assertion above pass having
        // examined nothing, which is the exact failure mode this file is about.
        var entries = DesktopEntries();
        Assert.True(entries.Count >= 15,
            $"Only {entries.Count} desktop screens were parsed out of Screens.cs, which is fewer than the list "
            + "has ever held — the scan has stopped seeing the list rather than the list having shrunk. Fix the "
            + "parse; do not lower this floor.");

        Assert.Contains(entries, e => e.Entry.Contains("Capture.Live"));
    }
}
