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
    private const string Index = "docs/adr/README.md";

    [GeneratedRegex(@"^\| (\d{4}) \| \[(?<title>.+?)\]\((?<href>[^)]+)\) \|")]
    private static partial Regex IndexRow();

    [GeneratedRegex(@"\[(?<label>\d{4})\]\((?<href>0\d{3}-[a-z0-9-]+\.md)\)")]
    private static partial Regex CrossLink();

    // EVERY ADR CROSS-LINK RESOLVES, AND ITS LABEL MATCHES ITS TARGET.
    //
    // Two failures, one guard, because they are the same mistake at different stages of the same sentence: an
    // author knows which ADR they mean and writes the filename from memory.
    //
    //   * a BROKEN TARGET — `[0557](0557-following-a-rel-costs-a-request.md)` when the file is
    //     `0557-following-a-rel-must-not-cost-a-request-per-rel.md`. Renders as a dead link; nothing else notices.
    //   * a MISLABELLED target — `[0842](0843-….md)`, citing the DECISION and pointing at the ABI slice that
    //     ships it. Worse than a dead link, because it renders fine and sends the reader to a real ADR that
    //     is not the one being cited. It also PROPAGATES: written once in 0851, it was copied into three
    //     later ADRs by authors following the house style of citing what you build on.
    //
    // Measured on the day this was written: 5 broken targets and 4 mislabels across one session's ADRs, every
    // one of them caught only by hand-checking after the fact. The numbers are the argument — a rule nothing
    // measures drifts, and this file already exists to say so about the index.
    [Fact]
    public void Every_adr_cross_link_resolves_and_is_labelled_with_its_targets_number()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root))
        {
            return; // the public mirror has no docs/, by design
        }

        var adrDir = Path.Combine(root, "docs", "adr");
        var present = Directory.GetFiles(adrDir, "0*.md").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var broken = new List<string>();
        var mislabelled = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.GetFiles(adrDir, "*.md"))
        {
            var name = Path.GetFileName(file);
            foreach (var link in CrossLink().Matches(File.ReadAllText(file)).Cast<Match>())
            {
                scanned++;
                var href = link.Groups["href"].Value;
                var label = link.Groups["label"].Value;

                if (!present.Contains(href))
                {
                    broken.Add($"{name}: [{label}]({href}) — no such ADR file");
                }
                else if (!href.StartsWith(label, StringComparison.Ordinal))
                {
                    mislabelled.Add($"{name}: [{label}]({href}) — the label says {label}, the target is {href[..4]}");
                }
            }
        }

        // Anti-vacuous: the ADRs cross-reference each other constantly, so a scan finding almost nothing means
        // the pattern stopped matching rather than that the links became perfect.
        Assert.True(scanned > 100, $"only {scanned} ADR cross-links matched — the pattern is probably broken");

        Assert.Empty(broken.Concat(mislabelled));
    }

    [Fact]
    public void The_index_stays_sorted_unique_and_one_to_one_with_the_adr_files()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root))
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
}
