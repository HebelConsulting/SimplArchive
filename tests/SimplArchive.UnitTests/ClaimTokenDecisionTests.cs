using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A CLAIM on a concurrency-tracked entity must DECIDE about the token (#1444).
//
// WHY THIS EXISTS, and it is not a style rule. `SaveChanges` regenerates `ConcurrencyToken` for every
// Added/Modified `IConcurrencyTracked` entity. `ExecuteUpdate` never touches the ChangeTracker, so none of that
// runs — which means converting a sweep to a claim (ADR 0836 asks every sweep to do exactly that) SILENTLY stops
// moving the token, and an `If-Match` write that used to be refused with a 412 starts succeeding.
//
// It went unnoticed through three conversions. Nothing failed, nothing logged, and the change is invisible in the
// diff: the line that used to move the token is the line that was deleted.
//
// WHAT IT CANNOT DO, stated so nobody reads more into a green run. The rule at `IConcurrencyTracked` is about
// VISIBILITY — move the token when the claim changes something the reader can see, leave it when it changes
// invisible bookkeeping — and no test can judge that. This asks only that somebody DECIDED, which is the part
// that actually went missing. A wrong decision passes here and is caught by a person.
//
// THE TARGET IS RESOLVED PER STATEMENT, not per file, and that distinction was measured rather than assumed —
// see ExecuteUpdateTargets. The per-file question was wrong 3 times out of 4.
//
// The DECISION is still read per file: a file that updates a tracked entity must contain either a ConcurrencyToken
// set or the marker. The cost is that a file with two updates needing DIFFERENT answers would pass with one of
// them; no file has that shape today, and a reader who creates one is the same reader who has just been made to
// think about the question.
public partial class ClaimTokenDecisionTests
{
    /// <summary>The marker a claim carries when it deliberately leaves the token alone.</summary>
    private const string Marker = "TOKEN DELIBERATELY NOT MOVED";

    [GeneratedRegex(@"class\s+(\w+)\s*:([^\{\r\n]*)")]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"DbSet<([\w\.]+)>\s+(\w+)\s*=>")]
    private static partial Regex DbSetDeclaration();

    [Fact]
    public void A_claim_on_a_tracked_entity_either_moves_the_token_or_says_why_not()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var src = Path.Combine(root, "src");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains("Migrations", StringComparison.Ordinal))
            .ToList();

        // Which entities are tracked, read from their own class declarations rather than a list kept here — a
        // hand-maintained copy is the thing that goes stale the day somebody adds the interface.
        var tracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match m in ClassDeclaration().Matches(File.ReadAllText(file)))
            {
                if (m.Groups[2].Value.Contains("IConcurrencyTracked", StringComparison.Ordinal))
                {
                    tracked.Add(m.Groups[1].Value);
                }
            }
        }

        Assert.True(tracked.Count > 10,
            $"Only {tracked.Count} tracked entities were found, which means the declaration scan stopped working "
            + "rather than that the codebase changed. A guard that finds nothing to check passes vacuously.");

        // …and which DbSet names reach them, so a claim can be matched to its entity by the property it uses.
        var context = files.Single(f => Path.GetFileName(f) == "SimplArchiveDbContext.cs");
        var trackedSets = DbSetDeclaration().Matches(File.ReadAllText(context))
            .Where(m => tracked.Contains(m.Groups[1].Value.Split('.')[^1]))
            .Select(m => m.Groups[2].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(trackedSets.Count > 5, $"Only {trackedSets.Count} tracked DbSets resolved — the model scan broke.");

        var undecided = new List<string>();
        var claims = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var decided = text.Contains("ConcurrencyToken", StringComparison.Ordinal)
                || text.Contains(Marker, StringComparison.Ordinal);

            foreach (var target in ExecuteUpdateTargets(text))
            {
                if (!trackedSets.Contains(target))
                {
                    continue;
                }

                claims++;
                if (!decided)
                {
                    undecided.Add($"{Path.GetFileName(file)} (updates {target})");
                    break;
                }
            }
        }

        // ANTI-VACUOUS. Without this the guard passes on the day the DbSet-matching stops working, which is the
        // failure mode its siblings were built to avoid.
        Assert.True(claims >= 3,
            $"Only {claims} file(s) were seen to claim on a tracked entity. There were three when this was "
            + "written, so a lower number means the matching broke rather than the claims went away.");

        Assert.True(undecided.Count == 0,
            "These ExecuteUpdate claims touch a concurrency-tracked entity and neither move the token nor say why "
            + "not:\n  " + string.Join("\n  ", undecided)
            + "\n\nExecuteUpdate does NOT regenerate ConcurrencyToken the way SaveChanges does, so a claim that "
            + "ignores it silently retires an If-Match refusal that used to fire. Decide by VISIBILITY (the rule "
            + "is at IConcurrencyTracked): if the claim changes something the reader can SEE, set the token in the "
            + $"same ExecuteUpdate; if it is invisible bookkeeping, say so with the marker `{Marker}` and the "
            + "reason. This guard cannot judge which — only that nobody decided.");
    }

    /// <summary>
    /// The DbSet each <c>ExecuteUpdateAsync</c> in this file is applied to.
    /// </summary>
    /// <remarks>
    /// <b>Scoped to the STATEMENT, and that is what makes this usable.</b> Asking only whether a file contains an
    /// <c>ExecuteUpdateAsync</c> and mentions a tracked DbSet anywhere was measured first and was wrong 3 times
    /// out of 4: the email dispatcher JOINS <c>Tenants</c> while updating an untracked queue, the reminder sweep
    /// reads <c>Users</c> while updating untracked reminders, and the notifications controller reads
    /// <c>Documents</c> while updating untracked notifications. A guard wrong three times in four is one that gets
    /// suppressed rather than read.
    /// <para>
    /// So the target is resolved from the statement the call belongs to — back to the previous <c>;</c>, <c>{</c>
    /// or <c>}</c> — and, when that names a local rather than a DbSet (<c>var rows = _dbContext.WorkflowStates…</c>
    /// then <c>rows.Where(…).ExecuteUpdateAsync(…)</c>), through that local's assignment. Those two shapes are
    /// what the codebase actually contains; a third would simply not resolve, and an unresolved call is skipped
    /// rather than guessed at.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> ExecuteUpdateTargets(string text)
    {
        foreach (Match call in Regex.Matches(text, @"ExecuteUpdateAsync"))
        {
            var start = text.LastIndexOfAny([';', '{', '}'], call.Index) + 1;
            var statement = text[start..call.Index];

            if (DbSetUse().Match(statement) is { Success: true } direct)
            {
                yield return direct.Groups[1].Value;
                continue;
            }

            // A local standing in for the set: take the first identifier of the statement and find where it was
            // assigned.
            if (Regex.Match(statement, @"[\w\.]*\b(\w+)\s*\r?\n?\s*\.") is not { Success: true } local)
            {
                continue;
            }

            var assignment = Regex.Match(text, $@"var\s+{Regex.Escape(local.Groups[1].Value)}\s*=(?:[^;]*?)"
                + @"_?[dD]bContext\.(\w+)", RegexOptions.Singleline);
            if (assignment.Success)
            {
                yield return assignment.Groups[1].Value;
            }
        }
    }

    [GeneratedRegex(@"_?[dD]bContext\.(\w+)")]
    private static partial Regex DbSetUse();
}
