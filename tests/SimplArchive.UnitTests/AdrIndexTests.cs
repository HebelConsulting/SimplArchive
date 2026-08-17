using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The ADR index (`docs/adr/README.md`) is append-only and every ADR PR appends to the same line region, so two ADR
// PRs in flight always conflicted on it — mechanically, pointlessly, and each time forcing a fresh CI cycle on the
// trailing PR. `.gitattributes` now marks the file `merge=union`, which keeps BOTH sides' lines instead of raising
// a conflict. That trade buys away the conflict at the cost of two things git will no longer tell us about, and
// this is the guard that tells us instead:
//
//   * ORDERING — a union-merged row lands after whatever the other side appended, so it can sit out of numeric
//     sequence (verified: merging a branch adding 0901 into one adding 0902 yields 0902, 0901).
//   * SILENT DUPLICATION — union resolves a genuine same-line disagreement by keeping both lines rather than
//     flagging it, so a hand-edit collision shows up as two rows for one ADR instead of a conflict.
//
// Generating the index from the ADR files was considered and rejected: the index is CURATED, not derived — 96 of
// its status cells and 16 of its titles are deliberately terser or more current than the files they point at, and
// for ADR 0406 the index is the ONLY record that ADR 0503 superseded it. Generation would have deleted that.
//
// PRIVATE-REPOSITORY ONLY, for the same reason as SeedClaudeLockstepTests: `tests/` is published byte-for-byte
// while `docs/` is withheld (ADR 0484), so the input is absent in the mirror by design. The gate is the ORIGIN
// REMOTE rather than the file's presence — "the file isn't there" must not be indistinguishable from "someone
// deleted it", because a guard that quietly passes when its input vanishes is not a guard.
public partial class AdrIndexTests
{
    private const string PrivateRepo = "HebelConsulting/SimplArchivePrivate";
    private const string Index = "docs/adr/README.md";

    [GeneratedRegex(@"^\| (\d{4}) \| \[(?<title>.+?)\]\((?<href>[^)]+)\) \|")]
    private static partial Regex IndexRow();

    [Fact]
    public void The_index_stays_sorted_unique_and_one_to_one_with_the_adr_files()
    {
        if (RepoRoot() is not { } root || !IsPrivateRepository(root))
        {
            return; // the public mirror has no docs/, by design
        }

        var indexPath = Path.Combine(root, Index.Replace('/', Path.DirectorySeparatorChar));
        var adrDir = Path.Combine(root, "docs", "adr");
        Assert.True(File.Exists(indexPath), $"{Index} is missing — this guard has nothing to check.");

        var rows = File.ReadAllLines(indexPath)
            .Select(l => IndexRow().Match(l))
            .Where(m => m.Success)
            .Select(m => (Number: int.Parse(m.Groups[1].Value), Href: m.Groups["href"].Value))
            .ToList();
        Assert.True(rows.Count > 500, $"Only {rows.Count} rows parsed out of {Index} — the row format changed and this guard stopped seeing the table.");

        // Ordering. A union merge appends the incoming row after the local one, so this is the assertion that
        // catches it. The fix is to sort the table numerically — never to relax this.
        var outOfOrder = rows.Zip(rows.Skip(1)).Where(p => p.First.Number >= p.Second.Number)
            .Select(p => $"  {p.First.Number:0000} is followed by {p.Second.Number:0000}").ToList();
        Assert.True(outOfOrder.Count == 0,
            "The ADR index is not in ascending order — most likely a `merge=union` auto-resolution put an\n"
            + "incoming row after a local one. Sort the table numerically:\n" + string.Join("\n", outOfOrder));

        // Duplication. Union keeps both sides of a same-line disagreement rather than conflicting, so a collision
        // surfaces here as one number appearing twice.
        var duplicates = rows.GroupBy(r => r.Number).Where(g => g.Count() > 1).Select(g => $"  {g.Key:0000} ({g.Count()} rows)").ToList();
        Assert.True(duplicates.Count == 0,
            "The ADR index lists the same ADR more than once — a `merge=union` resolution kept both sides of an\n"
            + "edit that genuinely disagreed. Keep the correct row and delete the other:\n" + string.Join("\n", duplicates));

        // One-to-one with the files. Union cannot drop a row, but a hand-resolved conflict can, and an index that
        // silently loses an ADR is the failure this whole file exists to prevent.
        var files = Directory.GetFiles(adrDir, "0*.md").Select(Path.GetFileName).Where(f => f is not null).ToHashSet()!;
        var linked = rows.Select(r => r.Href).ToHashSet();

        var missingFromIndex = files.Where(f => !linked.Contains(f!)).Order().ToList();
        Assert.True(missingFromIndex.Count == 0,
            $"These ADR files exist but no row in {Index} points at them:\n" + string.Join("\n", missingFromIndex.Select(f => $"  {f}")));

        var missingFromDisk = linked.Where(h => !files.Contains(h)).Order().ToList();
        Assert.True(missingFromDisk.Count == 0,
            $"{Index} links to files that do not exist (renamed or deleted without updating the row):\n"
            + string.Join("\n", missingFromDisk.Select(h => $"  {h}")));
    }

    private static bool IsPrivateRepository(string root)
    {
        // A worktree's .git is a file, and a source export has no .git at all — in either case the origin cannot
        // be established, so the guard stands down rather than guessing.
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
