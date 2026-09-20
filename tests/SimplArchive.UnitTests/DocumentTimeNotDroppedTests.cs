using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A document date copied from one carrier to another must bring its TIME (#1304).
//
// WHAT THIS IS FOR. A document's date is a PAIR — `DocumentDate` plus the optional `DocumentTime` (ADR 0758) —
// and they are one instant. Copying only the first is silent: the result is a date with no hour, which is a
// perfectly ordinary state that 22 writers in this solution produce on purpose. Absence proves nothing, so a
// dropped hour looks exactly like an hour that was never set, and nothing fails.
//
// It happened twice, in the same week, in unrelated code:
//
//   * `ImapMessageDetails` carried a bare `DateOnly`, so a METAR observed at 05:20 arrived in a mail client
//     dated MIDNIGHT — and a client sorts by that, so a day's observations came in arbitrary order.
//   * `IntrayController` built its staged draft from `version.DocumentDate` alone, so any item entering the
//     intray with an hour lost it at the door, with no way to put one back.
//
// Both were found by a person looking at a real document, which is not a strategy.
//
// CALIBRATION, because a guard that cries wolf gets suppressed rather than read. Three candidate signatures
// were measured against the tree before this one was chosen:
//
//   * "a type declaring DocumentDate must declare DocumentTime" — 1 hit, and it is a FALSE positive
//     (`VersionRowViewModel` holds an already-formatted string that includes the time).
//   * "any read of `.DocumentDate` without `.DocumentTime` nearby" — 33 hits, of which ~30 are legitimate:
//     eleven are the ENUM member `FolderContentsSortOrder.DocumentDate`, five are retention ANCHORS (a
//     retention clock is a date by design), eight are request-string validation. Wrong 30 times out of 33.
//   * this one — "a date copied from one carrier into another" — 13 hits, 13 of them correctly paired.
//
// The window is ±3 lines, and that number is measured rather than picked: ±1 and ±2 produce a false positive
// (one initializer separates the pair with a comment), ±3 upward produce none. Tighter is stronger, so ±3.
//
// WHERE THE COMPILER ALREADY WINS, and why this still earns its place. Both fixes were negative-controlled by
// reverting them. Reverting the INTRAY fix is caught here — its draft is built with an object initializer, so
// omitting a member compiles perfectly and the guard is the only thing that objects. Reverting the IMAP fix
// does not reach this test at all: `ImapMessageDetails` is a POSITIONAL record, so a missing argument is
// CS7036 and the build stops first.
//
// That is the division worth knowing when adding a new carrier: make it positional and the compiler enforces
// the pair for free; use an object initializer and this guard is your only cover. Prefer positional.
//
// WHAT IT DOES NOT COVER, stated so nobody trusts it further than it goes: a date copied through a helper
// method, a projection that renames the member, or a carrier that never had a time to begin with. It catches
// the shape both real defects had, not every way an hour could be lost.
public partial class DocumentTimeNotDroppedTests
{
    // `DocumentDate = x.DocumentDate` / `DocumentDate: x.DocumentDate` — a date being copied OUT of one object
    // and INTO another. Deliberately not a bare `.DocumentDate` read: that also matches the sort-order enum,
    // a retention anchor and a parse of a request string, none of which have an hour to lose.
    [GeneratedRegex(@"\bDocumentDate\s*[=:]\s*[A-Za-z_][\w.?!\[\]]*\.DocumentDate\b")]
    private static partial Regex CarrierCopy();

    [GeneratedRegex(@"\bDocumentTime\s*[=:]")]
    private static partial Regex TimeSibling();

    private const int Window = 3;

    /// <summary>
    /// Copies that are allowed to drop the time, each with the reason. EMPTY today, and that is the honest
    /// state: every carrier-to-carrier copy in the tree pairs the two. An entry here should be rare and should
    /// explain why the destination genuinely cannot hold an hour.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = [];

    [Fact]
    public void A_document_date_copied_between_carriers_brings_its_time()
    {
        var offenders = new List<string>();
        var examined = 0;

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // Comments are skipped, including the ones in this very file that quote the pattern. A guard
                // that punishes documenting itself teaches people to stop documenting.
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')
                    || !CarrierCopy().IsMatch(lines[i]))
                {
                    continue;
                }

                examined++;
                var from = Math.Max(0, i - Window);
                var to = Math.Min(lines.Length - 1, i + Window);
                if (TimeSibling().IsMatch(string.Join('\n', lines[from..(to + 1)])))
                {
                    continue;
                }

                var where = $"{Relative(file)}:{i + 1}";
                if (!Allowed.ContainsKey(where))
                {
                    offenders.Add($"  {where}\n      {lines[i].Trim()}");
                }
            }
        }

        // ANTI-VACUOUS. A pattern that stops matching — a rename, a refactor into a helper — would make this
        // test pass while watching nothing, which is the failure mode of every source-scanning guard. The
        // count is the instrument: if it drops to zero, the guard has gone blind rather than the code clean.
        Assert.True(examined >= 10,
            $"only {examined} carrier-to-carrier document-date copies were found, where 13 were measured when "
            + "this was written. The pattern has probably stopped matching — fix the pattern rather than "
            + "trusting the green run.");

        Assert.True(offenders.Count == 0,
            "A document date is copied between carriers WITHOUT its time. The pair is one instant (ADR 0758), "
            + "and dropping half of it is silent: a date with no hour is indistinguishable from one that never "
            + "had an hour, so nothing fails and nobody notices until they look at a real document.\n\n"
            + string.Join("\n", offenders)
            + "\n\nCopy DocumentTime alongside it. If the destination genuinely cannot hold a time, add the "
            + "site to this test's Allowed list with the reason.");
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains("Migrations", StringComparison.Ordinal));
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(RepoRoot(), file).Replace(Path.DirectorySeparatorChar, '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
