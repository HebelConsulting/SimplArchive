using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// An entity that a PUT EDITS must carry a concurrency token — or say, here, why it does not (#1220).
//
// WHY THIS EXISTS BESIDE ConcurrencyContractRatchetTests RATHER THAN INSIDE IT. That guard asks whether a
// mutation of a TRACKED entity goes through the entity's verb contract, so it keys on the set of entities that
// already carry a token. An entity nobody ever tracked is invisible to it BY CONSTRUCTION — which is not a
// hypothetical: the debt list was emptied and reported 0 while `Group` sat here with a rename endpoint, no
// token, and no way for that guard to notice. Two admins renaming one group silently reverted each other, and
// `PUT /groups/{id}/rights` did the same to AUTHORIZATION, which is the worst place for a silent revert.
//
// WHAT IT MEASURES, stated honestly: a DbSet named in a controller that has a PUT action AND assigns a
// property on a materialised instance of that entity in the same file. It cannot prove the assignment belongs
// to the PUT rather than to a POST beside it — that needs the compiler, not a regex — so it is a prompt to
// classify, not a proof of a defect.
//
// IT IS CALIBRATED, and that is the difference between a guard people read and one they suppress. The obvious
// form — "a controller has a PUT anywhere and names this DbSet" — reported ELEVEN candidates of which TWO were
// real. Requiring the assignment cuts it to the two that matter. A guard wrong nine times out of eleven does
// not get fixed, it gets ignored, and then it is worse than nothing because its silence reads as safety.
public partial class UntrackedEditedEntityTests
{
    // Entities without a token, and why that is correct. The three shapes CLAUDE.md names, each checked
    // against the code rather than assumed:
    //
    //   - a JOIN ROW, whose only operations are create and delete: no in-place edit to collide on
    //   - APPEND-ONLY or MACHINE-OWNED: nobody edits it, so there is no lost update to lose
    //   - a CHILD GUARDED BY A TRACKED PARENT: the parent's token already covers the write
    //
    // Checked from the other side, like the sibling guard's ReadsOnly: an entry that stops being flagged is
    // removed rather than left standing, because a claim nobody checks is indistinguishable from a stale one.
    private static readonly Dictionary<string, string> NoTokenNeeded = new(StringComparer.Ordinal)
    {
        ["DocumentVersion"] =
            "Child guarded by a tracked parent — TRUE AS OF #1220, and it was not before. This entry first "
            + "read 'append-only: nothing edits a stored version in place', which was written from the "
            + "entity's design intent rather than its call sites and was simply false: PUT .../document-date "
            + "wrote DocumentDate/DocumentTime with a raw SaveChanges, honouring no If-Match and not moving "
            + "the document's token either. That PUT now runs through DocumentVerbs with touchEntity, so the "
            + "DOCUMENT's precondition covers it — which is the right tag to ask for, since the editor holds "
            + "the document's ETag. A token of its own would put TWO preconditions on one user action, which "
            + "ADR 0794 forbids. The finalize comment stays outside on purpose (fill-once, and its quota "
            + "cleanup must commit before its own refusal) — see the comment at that call site.",
        ["FieldValue"] =
            "Child guarded by a tracked parent. The index-data write moves the DOCUMENT's token — that is "
            + "exactly what EntityVerbContract's touchEntity is for (#1167) — so a per-value token would be a "
            + "second precondition on one user action, which ADR 0794 forbids.",
        ["FieldDefinition"] =
            "Belongs to a MaskVersion, and mask versions are IMMUTABLE by design (ADR 0166): a change makes a "
            + "new version rather than editing this one.",
        ["MaskVersion"] =
            "Immutable by design (ADR 0166) — auto-numbered by SaveChanges and never edited in place.",
        ["DocumentTag"] =
            "Join row: a tag is put on a document or taken off it, never edited.",
        ["GroupMembership"] =
            "Join row: a membership is created or deleted.",
        ["DocumentReference"] =
            "Join row: a reference is placed or removed. Promoting one to the primary location writes the "
            + "DOCUMENT's parent, which is tracked.",
        ["WorkflowTransition"] =
            "Append-only log of what happened. The state it describes is WorkflowState, which IS tracked.",
        ["UserProfilePhoto"] =
            "Replace-only child of a tracked user: a new photo replaces the row wholesale.",
    };

    [GeneratedRegex(@"\[HttpPut")]
    private static partial Regex PutAction();

    /// <summary>An assignment to a property of a materialised instance — `x.Name = …`, not an initialiser.</summary>
    [GeneratedRegex(@"^\s+[a-z][A-Za-z]*\.[A-Z][A-Za-z]*\s*=\s*[^=]", RegexOptions.Multiline)]
    private static partial Regex PropertyAssignment();

    [Fact]
    public void An_entity_edited_by_a_PUT_carries_a_concurrency_token()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var dbContextText = File.ReadAllText(Path.Combine(
            root, "src", "SimplArchive.Infrastructure", "Persistence", "SimplArchiveDbContext.cs"));

        // entity name -> DbSet name, read from the context rather than pluralised. Naive pluralisation is how
        // AclEntry went unchecked in the sibling guard for its whole life: `AclEntrys` matches nothing.
        var dbSets = DbSetDeclaration().Matches(dbContextText)
            .Select(m => (Entity: m.Groups[1].Value, Set: m.Groups[2].Value))
            .DistinctBy(x => x.Entity)
            .ToList();

        Assert.True(dbSets.Count > 30,
            $"Only {dbSets.Count} DbSets found — the context's shape changed and this guard stopped seeing it.");

        var domainDir = Path.Combine(root, "src", "SimplArchive.Domain");
        var tracked = Directory.GetFiles(domainDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("IConcurrencyTracked", StringComparison.Ordinal))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(tracked.Count > 10,
            $"Only {tracked.Count} tracked entities found — the Domain layout changed and this guard is blind.");

        var controllers = Directory.GetFiles(
            Path.Combine(root, "src", "SimplArchive.Api", "Controllers"), "*Controller.cs")
            .Select(p => (Name: Path.GetFileName(p), Text: File.ReadAllText(p)))
            .Where(c => PutAction().IsMatch(c.Text) && PropertyAssignment().IsMatch(c.Text))
            .ToList();

        var edited = dbSets
            .Where(s => !tracked.Contains(s.Entity))
            .Where(s => controllers.Any(c => c.Text.Contains($"_dbContext.{s.Set}", StringComparison.Ordinal)))
            .Select(s => s.Entity)
            .Where(e => !NoTokenNeeded.ContainsKey(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(edited.Count == 0,
            "These entities are EDITED by a controller with a PUT and carry no concurrency token, so two\n"
            + "people editing one row silently revert each other — no error, no log line, nothing to point at:\n"
            + string.Join("\n", edited.Select(e => $"  {e}"))
            + "\n\nGive the entity IConcurrencyTracked and route its writes through the entity's verb contract"
            + "\n(ADR 0795) — remembering that a tracked entity whose endpoints never CHECK the token is no"
            + "\nbetter than an untracked one. Or, if it genuinely needs none, add it to NoTokenNeeded WITH THE"
            + "\nREASON, in this same commit.");

        // The other direction: a claim that is no longer flagged is a claim nobody is checking.
        var stale = NoTokenNeeded.Keys
            .Where(e => !dbSets.Any(s => s.Entity == e) || tracked.Contains(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These NoTokenNeeded entries are tracked now, or no longer have a DbSet, so their stated reason is\n"
            + "unverifiable. Remove them:\n"
            + string.Join("\n", stale.Select(e => $"  {e}")));
    }

    [GeneratedRegex(@"DbSet<([A-Za-z]+)>\s+([A-Za-z]+)")]
    private static partial Regex DbSetDeclaration();
}
