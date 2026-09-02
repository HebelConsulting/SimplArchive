using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The desktop half of the guided tour's anchor contract (issue #414, ADR 0543's model of a stable name).
//
// The tour publishes anchors as bare, surface-neutral names — `pane-list` — because one step serves BOTH
// clients: the browser looks the name up as `data-tour`, the desktop client as an accessibility automation id
// prefixed `tour:`. The web side is guarded by WebGuidedTourTests against the running app; this is the desktop
// side, and it guards the same names against the desktop's view definitions.
//
// Why a STATIC scan rather than building the window headlessly and walking its tree. A tree walk sounds
// stronger and is in fact weaker here: Avalonia realises a ContextMenu's items only when the menu is opened, so
// `action-manage-access` — a real, working anchor — would be reported missing by a walk of the resting tree.
// A guard that reports working anchors as broken gets its failures ignored, and then it guards nothing. The
// scan below cannot prove a control is reachable at runtime, which the tour says plainly to the agent reading
// it; what it CAN prove is the thing that actually breaks, which is somebody renaming or deleting an anchor
// while reorganising a view.
public partial class DesktopGuidedTourTests
{
    // Anchors as the desktop exposes them: AutomationProperties.AutomationId="tour:<name>".
    [GeneratedRegex(@"AutomationProperties\.AutomationId=""tour:(?<anchor>[a-z0-9-]+)""")]
    private static partial Regex DesktopAnchor();

    // The tour's step blocks — the same shape WebGuidedTourTests parses, deliberately, so the two guards
    // cannot disagree about what the tour says.
    [GeneratedRegex(@"^(?:anchor|action|expect):.*$", RegexOptions.Multiline)]
    private static partial Regex StepLine();

    [GeneratedRegex(@"`(?<anchor>(?:pane|tab|action)-[a-z0-9-]+)`")]
    private static partial Regex TourAnchor();

    [Fact]
    public void Every_anchor_the_desktop_track_names_is_defined_in_the_desktop_views()
    {
        var root = RepoPaths.Root();
        var tourPath = Path.Combine(root, "src", "SimplArchive.Client", "wwwroot", "tour", "tour.md");
        Assert.True(File.Exists(tourPath), $"The published tour is missing: {tourPath}");

        var tour = File.ReadAllText(tourPath);

        // Only the steps the DESKTOP track performs. A quick-track-only step (the close that speaks to a
        // visitor on the shared public demo) names anchors the desktop client need not carry.
        var desktopAnchors = StepBlocks(tour)
            .Where(block => TracksOf(block).Contains("desktop"))
            .SelectMany(block => StepLine().Matches(block)
                .SelectMany(line => TourAnchor().Matches(line.Value).Select(m => m.Groups["anchor"].Value)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        // Anti-vacuous, and sample-independent: if the parse breaks, the assertion below would pass on nothing.
        Assert.True(desktopAnchors.Count >= 6,
            $"parsed only {desktopAnchors.Count} desktop anchors from the tour — the scan is broken, not the app");

        var defined = Directory
            .EnumerateFiles(Path.Combine(root, "src", "SimplArchive.DesktopClient"), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => DesktopAnchor().Matches(File.ReadAllText(f)).Select(m => m.Groups["anchor"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = desktopAnchors.Where(a => !defined.Contains(a)).ToList();

        Assert.True(missing.Count == 0,
            $"The desktop track names {missing.Count} anchor(s) the desktop views do not define: "
            + $"{string.Join(", ", missing)}.\nEither add AutomationProperties.AutomationId=\"tour:<name>\" to the "
            + "control the step means, or correct the step — the anchor names are a published contract (issue #414).");
    }

    // The reverse direction: an anchor defined but named by no step is dead weight on a public contract, and
    // the next person cannot tell whether it is safe to remove. Either the tour uses it or it goes.
    [Fact]
    public void Every_desktop_anchor_is_named_by_the_tour()
    {
        var root = RepoPaths.Root();
        var tour = File.ReadAllText(Path.Combine(root, "src", "SimplArchive.Client", "wwwroot", "tour", "tour.md"));

        var named = StepLine().Matches(tour)
            .SelectMany(line => TourAnchor().Matches(line.Value).Select(m => m.Groups["anchor"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var orphans = Directory
            .EnumerateFiles(Path.Combine(root, "src", "SimplArchive.DesktopClient"), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => DesktopAnchor().Matches(File.ReadAllText(f)).Select(m => m.Groups["anchor"].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(a => !named.Contains(a))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            $"These desktop anchors are named by no tour step: {string.Join(", ", orphans)}. "
            + "Use them in a step or remove them — an unused anchor on a published contract cannot be retired safely later.");
    }

    private static IEnumerable<string> StepBlocks(string tour) =>
        tour.Split("```").Where(b => b.Contains("tracks:", StringComparison.Ordinal));

    private static string TracksOf(string block)
    {
        var line = block.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("tracks:", StringComparison.Ordinal));
        return line ?? "";
    }

}
