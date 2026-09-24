using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

/// <summary>
/// The desktop manual screenshots are rendered from a hand-written fixture, and the web ones are captured
/// against the really-seeded app. Nothing connected the two, so the manual documented a dataset that does not
/// exist: the workbench screenshot showed <c>Invoice 2025-001.pdf</c> and <c>sample.docx</c> while the seeder
/// creates <c>Invoice 2026-003</c> and <c>Offer 2026-014</c> — two halves of one manual describing two
/// different products.
///
/// The drift is silent by construction: a fixture cannot fail when the product moves. So this asserts the one
/// property that matters — every DOCUMENT the fixture shows is a document the demo seeder actually creates.
///
/// It deliberately does NOT check folders or tree nodes. Those legitimately illustrate shapes the seed has no
/// single instance of (an empty folder, a reference), and a guard that forbade them would be a guard against
/// explaining the product.
/// </summary>
public sealed partial class ManualFixtureMatchesSeedTests
{
    /// <summary>
    /// Fixture documents with deliberately no seeded counterpart, each with the reason it illustrates.
    /// An entry here is a claim that the screenshot is showing a SHAPE, not a document.
    /// </summary>
    private static readonly Dictionary<string, string> Illustrative = new(StringComparer.Ordinal)
    {
        ["Shared Contract.pdf"] = "a reference node (IsReference) — shows the reference glyph, not a real seeded document",
    };

    [Fact]
    public void Every_document_in_the_desktop_fixture_is_one_the_seeder_creates()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var fixturePath = Path.Combine(root, "src", "SimplArchive.DesktopClient", "ViewModels", "MainWindowViewModel.Screenshots.cs");
        var seederPath = Path.Combine(root, "src", "SimplArchive.Api", "Provisioning", "DemoDataSeeder.cs");
        Assert.True(File.Exists(fixturePath), $"{fixturePath} is missing — the desktop screenshot fixture moved; this guard must move with it.");
        Assert.True(File.Exists(seederPath), $"{seederPath} is missing — the demo seeder moved; this guard must move with it.");

        var seeder = File.ReadAllText(seederPath);
        var fixture = File.ReadAllText(fixturePath);

        // A document in the fixture is a node that HAS versions — that is what distinguishes it from a folder.
        var documents = DocumentNode().Matches(fixture)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(documents); // an empty expectation would pass while measuring nothing

        var missing = documents
            .Where(name => !Illustrative.ContainsKey(name))
            .Where(name => !seeder.Contains($"\"{name}\"", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "The desktop manual fixture shows documents the demo seeder never creates, so the manual's desktop "
            + "screenshots describe a different dataset than its web ones:\n  "
            + string.Join("\n  ", missing)
            + "\n\nUse a name DemoDataSeeder actually files, or — if the screenshot is illustrating a SHAPE rather "
            + "than a document — add it to Illustrative with the reason.");
    }

    /// <summary>A fixture list entry that carries versions: <c>Name = "X", … HasVersions = true</c>.</summary>
    [GeneratedRegex(@"new NodeViewModel\s*\{[^}]*?Name\s*=\s*""(?<name>[^""]+)""[^}]*?HasVersions\s*=\s*true[^}]*?\}")]
    private static partial Regex DocumentNode();
}
