using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Every line of the manual's "What is still open" names the issue that tracks it (issue #1592).
//
// The security appendix listed seven open items while five had shipped — and shipped in the very release whose
// manual said they had not, because nothing ever asked whether a manual SENTENCE was still true. The cure is two
// halves: this test makes each line carry an issue number, and the weekly `security-open-items` workflow fails
// when one of those issues has closed. Without this half the weekly check has nothing to look up, and a line
// written without a citation would be invisible to it — the decay it exists to catch, re-admitted by omission.
//
// The manual is PUBLISHED (manual/ is not withheld, ADR 0484), so this runs on the mirror too; it needs no network.
public partial class ManualOpenItemsCiteAnIssueTests
{
    // Typst treats '#' as code, so the manual writes "\#1578"; the unescaped form would not compile, but is accepted
    // here so the rule is about citing, not about escaping.
    [GeneratedRegex(@"\\?#\d{3,}")]
    private static partial Regex IssueCitation();

    [Fact]
    public void Every_open_item_names_the_issue_that_tracks_it()
    {
        var manual = Path.Combine(RepoPaths.Root(), "manual", "manual.typ");
        var lines = File.ReadAllLines(manual);

        var start = Array.FindIndex(lines, l => l.StartsWith("== What is still open", StringComparison.Ordinal));
        Assert.True(start >= 0,
            "manual.typ has no '== What is still open' heading. If it was renamed, update this test AND "
            + "scripts/check-security-open-items.sh, which looks for the same heading.");

        var bullets = new List<string>();
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("= ", StringComparison.Ordinal)
                                                  && !lines[i].StartsWith("== ", StringComparison.Ordinal); i++)
        {
            if (lines[i].StartsWith("- ", StringComparison.Ordinal))
            {
                bullets.Add(lines[i]);
            }
            else if (lines[i].StartsWith("  ", StringComparison.Ordinal) && bullets.Count > 0)
            {
                bullets[^1] += " " + lines[i].Trim();
            }
        }

        // Anti-vacuous: a parse that finds no bullet would pass while checking nothing.
        Assert.True(bullets.Count >= 1,
            "'What is still open' yielded no bullets. If nothing is open, say so in prose and retire this test "
            + "together with the weekly workflow — do not let either pass on an empty list.");

        var uncited = bullets.Where(b => !IssueCitation().IsMatch(b)).ToList();
        Assert.True(uncited.Count == 0,
            "Every line of 'What is still open' must name the issue tracking it, written \\#NNNN, so the weekly "
            + "check can tell when it has been delivered:\n  " + string.Join("\n  ", uncited));
    }
}
