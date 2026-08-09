using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// `docs/SeedCLAUDE.md` is the portable extract of this project's way of working — a drop-in starting CLAUDE.md
// for another repository. It is only worth having if it stays true, and a document that must be remembered is a
// document that goes stale: the standing principles here have accumulated one at a time, each from a specific
// incident, which is exactly the pattern that leaves a copy behind.
//
// So the link is mechanical. Every TITLED standing marker in CLAUDE.md — `**Standing principle — …:**`,
// `**Standing convention — …:**`, `**Standing rule — …:**` — must be accounted for in the seed, either as
//
//     <!-- carries: <the exact title> -->            the principle is carried, generalised
//     <!-- not-portable: <the exact title> — why -->  a deliberate omission, with its reason
//
// Adding a standing principle to CLAUDE.md therefore fails the build until the seed either carries it or states
// why it does not. Untitled markers ("Standing conventions (follow these…)") carry no name to match on and are
// out of scope; the seed says as much, so a green build reads as "no standing principle was forgotten", not "the
// seed is complete".
//
// PRIVATE-REPOSITORY ONLY. `tests/` is published byte-for-byte to the public mirror while `CLAUDE.md` and
// `docs/` are withheld (ADR 0484), so over there both inputs are absent by design. The gate is the ORIGIN
// REMOTE, not the presence of the files: "the file isn't there" is indistinguishable from "someone deleted it",
// and a guard that quietly passes when its input vanishes is the failure mode this repo already has a name for
// (a skipped check is not a passing check). Inside the private repo the inputs are REQUIRED — a missing
// CLAUDE.md or seed fails loudly rather than skipping.
public partial class SeedClaudeLockstepTests
{
    private const string PrivateRepo = "HebelConsulting/SimplArchivePrivate";
    private const string Seed = "docs/SeedCLAUDE.md";

    // The em-dash form is what carries a title; the trailing "(`docs/adr/0543`)" reference is not part of it.
    [GeneratedRegex(@"\*\*Standing (?:principle|convention|rule)s? — (.+?)(?:\s*\(`docs/adr/\d+`\))?:\*\*")]
    private static partial Regex StandingMarker();

    [Fact]
    public void Every_standing_principle_is_carried_or_explicitly_not_portable()
    {
        if (RepoRoot() is not { } root || !IsPrivateRepository(root))
        {
            return; // the public mirror has neither input, by design
        }

        var claudeMd = Path.Combine(root, "CLAUDE.md");
        var seedPath = Path.Combine(root, Seed.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(claudeMd), $"CLAUDE.md is missing from {root} — this guard has nothing to check.");
        Assert.True(File.Exists(seedPath), $"{Seed} is missing — it is the portable extract this guard keeps honest.");

        var seed = File.ReadAllText(seedPath);
        var titles = StandingMarker().Matches(File.ReadAllText(claudeMd)).Select(m => m.Groups[1].Value.Trim()).ToList();

        // Anti-vacuous: not a count that grows with the work (see LocalizationLiteralTests for why that is a trap),
        // just proof the marker shape still parses at all.
        Assert.True(titles.Count > 0,
            "No standing markers were found in CLAUDE.md — the heading shape changed and this guard is now blind. "
            + "Fix StandingMarker() rather than deleting the assertion.");

        var missing = titles
            .Where(t => !seed.Contains($"<!-- carries: {t} -->", StringComparison.Ordinal)
                     && !seed.Contains($"<!-- not-portable: {t} ", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            $"CLAUDE.md standing principles with no counterpart in {Seed}:\n"
            + string.Join("\n", missing.Select(t => $"  {t}"))
            + $"\n\nCarry each into {Seed} in project-agnostic form with a marker comment"
            + "\n  <!-- carries: <title> -->\nor record why it does not transfer"
            + "\n  <!-- not-portable: <title> — <reason> -->");
    }

    // Marker comments must name a principle that actually exists, so a reworded heading cannot leave the seed
    // pointing at nothing while still passing the check above.
    [Fact]
    public void The_seeds_markers_all_name_a_real_standing_principle()
    {
        if (RepoRoot() is not { } root || !IsPrivateRepository(root))
        {
            return;
        }

        var titles = StandingMarker().Matches(File.ReadAllText(Path.Combine(root, "CLAUDE.md")))
            .Select(m => m.Groups[1].Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var seed = File.ReadAllText(Path.Combine(root, Seed.Replace('/', Path.DirectorySeparatorChar)));

        var dangling = Regex.Matches(seed, @"<!-- (?:carries|not-portable): (.+?) (?:-->|— )")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(t => !titles.Contains(t))
            .ToList();

        Assert.True(dangling.Count == 0,
            $"{Seed} names principles that no longer appear in CLAUDE.md (reworded or removed):\n"
            + string.Join("\n", dangling.Select(t => $"  {t}"))
            + "\n\nUpdate the marker to the new wording, or drop the section if the principle is gone.");
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
