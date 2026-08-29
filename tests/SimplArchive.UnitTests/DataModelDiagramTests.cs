using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// `docs/data-model.md` draws the physical schema, and a curated diagram rots the moment somebody adds a table:
// nothing in a build fails when a picture stops matching the thing it pictures, and nobody re-derives a diagram
// they have no reason to distrust. So this guard keeps the CAST honest in both directions — every entity with an
// EF configuration is drawn, and nothing is drawn that no longer has one.
//
// What it deliberately does NOT check is the EDGES. Whether `LegalHoldItem.DocumentId` really is `Restrict` is a
// fact about the configuration that a test could assert, but the diagram's value is in the sentences around the
// arrows, and a guard that pinned every arrow would fail on every schema change while teaching nobody anything.
// The delete behaviours in that document were extracted from the configuration rather than remembered; the
// reader is the check on them, and this is the check that the reader is looking at a complete picture.
//
// Private-repository-only, on the same footing as AdrIndexTests: `docs/` is withheld from the public mirror
// (ADR 0484) while `tests/` is published byte-for-byte, so in the mirror this guard's input does not exist by
// design. Note it also stands down inside a git WORKTREE, whose `.git` is a file rather than a directory (#583)
// — so a green run in a worktree proves nothing, and this one was verified from a full clone.
public partial class DataModelDiagramTests
{
    private const string PrivateRepo = "HebelConsulting/SimplArchivePrivate";
    private const string Doc = "docs/data-model.md";

    // A node either sits on one side of a relationship line — `Parent ||--o{ Child : "label"` — or is declared
    // as a block with its own attributes. Both forms count as "drawn".
    // The cardinality pair is the FULL mermaid vocabulary, not the one shape the first draft happened to use:
    // `||--o{` and `|o--o{` and `||--o|` are all relationship lines, and a parser that knows only the common one
    // reports the others as undrawn. Which is exactly what it did on the first run — against a diagram that DID
    // draw the entity, one-to-one.
    [GeneratedRegex(@"^\s*(?<left>\w+)\s+[|}o][|o]--[|o][|o{]\s*(?<right>\w+)", RegexOptions.Multiline)]
    private static partial Regex RelationshipLine();

    [GeneratedRegex(@"^\s{4}(?<node>\w+)\s*\{\s*$", RegexOptions.Multiline)]
    private static partial Regex BlockNode();

    [Fact]
    public void Every_configured_entity_is_drawn_and_nothing_else_is()
    {
        if (RepoRoot() is not { } root || !IsPrivateRepository(root))
        {
            return; // the public mirror has no docs/, by design
        }

        var docPath = Path.Combine(root, Doc.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(docPath), $"{Doc} is missing — this guard has nothing to check.");

        var configDir = Path.Combine(root, "src", "SimplArchive.Infrastructure", "Persistence", "Configurations");
        var configured = Directory.GetFiles(configDir, "*Configuration.cs")
            .Select(f => Path.GetFileNameWithoutExtension(f)[..^"Configuration".Length])
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(configured.Count > 20, $"only {configured.Count} entity configurations found — the guard is looking in the wrong place.");

        var text = File.ReadAllText(docPath);
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in RelationshipLine().Matches(text))
        {
            drawn.Add(m.Groups["left"].Value);
            drawn.Add(m.Groups["right"].Value);
        }

        foreach (Match m in BlockNode().Matches(text))
        {
            drawn.Add(m.Groups["node"].Value);
        }

        var missing = configured.Except(drawn).Order().ToList();
        var stale = drawn.Except(configured).Order().ToList();

        Assert.True(
            missing.Count == 0,
            $"{Doc} does not draw these entities, which have an EF configuration:\n"
            + string.Join("\n", missing.Select(e => $"  {e}"))
            + "\n\nPlace each in the group it belongs to — a table nobody drew is a table nobody knows about.");

        Assert.True(
            stale.Count == 0,
            $"{Doc} draws these, which have no EF configuration any more:\n"
            + string.Join("\n", stale.Select(e => $"  {e}"))
            + "\n\nEither the name is misspelled (so the entity it means is silently missing too), or the table is "
            + "gone and the diagram still describes it.");
    }

    private static bool IsPrivateRepository(string root)
    {
        // A worktree's .git is a file, and a source export has no .git at all — in either case the origin cannot
        // be established, so the guard stands down rather than guessing (#583).
        var config = Path.Combine(root, ".git", "config");
        return File.Exists(config)
            && File.ReadAllText(config).Contains(PrivateRepo, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
