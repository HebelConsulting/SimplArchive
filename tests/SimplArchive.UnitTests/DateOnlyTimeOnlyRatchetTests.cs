using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A new DateOnly or TimeOnly in the domain has to say which clock it is on (#1254).
//
// WHY A RATCHET AND NOT A GUARD. The invariant you would actually want — "this value is UTC" — cannot be
// enforced anywhere. A TimeOnly of 10:00 stored from Zurich wall-clock and a TimeOnly of 10:00 that is
// genuinely UTC are THE SAME BITS; SaveChanges has nothing to test, and neither does the database. Every other
// temporal type is better protected than these two: a DateTimeOffset is normalised by
// UtcDateTimeOffsetConverter on the way in and then REFUSED by Npgsql if it is not UTC — a driver-level check
// no hand-written guard improves on.
//
// So the only moment the distinction is visible is where the value is CONVERTED, and the only protection is a
// test at that site. This ratchet does not pretend to verify such a test exists — it cannot, and a check that
// claims more than it measures is worse than none. What it does is make a NEW property a decision somebody has
// to take deliberately, in this file, with the answer written down.
//
// THAT IS THE ACTUAL FAILURE MODE. The calendar classifier stores wall-clock time in a field documented as UTC,
// and it got there when somebody added a field — not when somebody broke a rule. A rule nobody could break is
// exactly what let it happen quietly.
public class DateOnlyTimeOnlyRatchetTests
{
    /// <summary>
    /// Every <c>DateOnly</c>/<c>TimeOnly</c> the domain stores, and which clock each is on.
    /// </summary>
    /// <remarks>
    /// Keyed by <c>Type.Property</c>. The value is not decoration: it is the answer a reader needs when they
    /// meet the field, and the thing whoever adds the next one is being asked to decide.
    /// </remarks>
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["DocumentVersion.DocumentDate"] =
            "UTC, and only together with DocumentTime — the two are ONE INSTANT split across two columns, so "
            + "converting for display moves the DATE as well (23:30 UTC on the 16th is the 17th in Zurich).",

        ["DocumentVersion.DocumentTime"] =
            "UTC. Cannot be verified from the value — see the class comment. Its conversion sites are "
            + "DocumentFinalizer (e-mail: .UtcDateTime, correct) and CalendarContactClassifier (.Value, which "
            + "is the entry's WALL CLOCK — #1254).",

        ["Document.RetentionOverrideUntil"] =
            "A DATE, deliberately not an instant: 'retain until' is a calendar day a records manager chose, "
            + "compared against the sweep's date. It has no time-of-day to be wrong about, which is why it "
            + "needs no partner column.",
    };

    [Fact]
    public void Every_date_or_time_only_property_in_the_domain_is_accounted_for()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var found = Declarations(Path.Combine(root, "src", "SimplArchive.Domain"));

        // ANTI-VACUOUS, and the reason it is here rather than assumed: a regex that stops matching turns this
        // into a test that passes by seeing nothing, which is the exact shape of guard this codebase keeps
        // paying for. Three at the time of writing; the bound is loose so ADDING one fails below, where the
        // message is, rather than here.
        Assert.True(found.Count >= 3,
            $"Only {found.Count} DateOnly/TimeOnly properties found in the domain — the scan stopped seeing "
            + "them, so every assertion below would pass while checking nothing. Fix the pattern, not this.");

        var undeclared = found.Where(f => !Known.ContainsKey(f.Key)).ToList();

        Assert.True(undeclared.Count == 0,
            "These DateOnly/TimeOnly properties are new and nothing says which clock they are on:\n"
            + string.Join("\n", undeclared.Select(f => $"  {f.Key}  ({f.Value})"))
            + "\n\nA DateOnly or TimeOnly carries no offset, so NOTHING can check it later — not SaveChanges, "
            + "not the database, not a reviewer reading the value. Decide now:\n\n"
            + "  • a UTC instant?  then it needs its date/time PARTNER, and a test at the conversion site "
            + "proving a zoned source lands as UTC (a 10:00 Zurich .ics must store 08:00, not 10:00)\n"
            + "  • a calendar DATE a human chose?  then it must never be shifted for display\n\n"
            + "Then add it to Known above with that answer. The calendar bug in #1254 arrived exactly this "
            + "way — as a new field, not as a broken rule.");
    }

    [Fact]
    public void The_ledger_does_not_name_properties_that_are_gone()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        // The other direction, and the one a ratchet usually forgets: an entry for a property that no longer
        // exists is a claim nobody can check, and it quietly raises the bar for the anti-vacuous count above.
        var found = Declarations(Path.Combine(root, "src", "SimplArchive.Domain"));
        var stale = Known.Keys.Where(k => !found.ContainsKey(k)).ToList();

        Assert.True(stale.Count == 0,
            "These entries name properties the domain no longer has:\n"
            + string.Join("\n", stale.Select(s => $"  {s}"))
            + "\n\nRemove them — a ledger that outlives what it describes stops being read.");
    }

    private static Dictionary<string, string> Declarations(string domainRoot)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(domainRoot, "*.cs", SearchOption.AllDirectories))
        {
            var type = Path.GetFileNameWithoutExtension(file);
            foreach (var line in File.ReadLines(file))
            {
                // Deliberately tolerant of the nullable marker: `DateOnly?` and `DateOnly` are the same
                // question. Comment lines are skipped so documenting the rule cannot trip it.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                var match = Regex.Match(line, @"public\s+(DateOnly|TimeOnly)\??\s+(\w+)\s*\{");
                if (match.Success)
                {
                    found[$"{type}.{match.Groups[2].Value}"] = match.Groups[1].Value;
                }
            }
        }

        return found;
    }
}
