using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A controller carrying a verdict must be RE-READ when it grows a new way to mutate (#1224).
//
// THE GAP THIS FILLS, precisely. `ConcurrencyContractRatchetTests` carries verdicts — ReadsOnly, CreatesOnly,
// PermanentlyExempt — and checks each from the other side: an entry that stops being flagged is removed,
// because a claim nobody checks is indistinguishable from a stale one. That catches a controller RENAMED,
// DELETED, or no longer touching the entity.
//
// It cannot catch the thing most likely to happen: a controller that was exempt because it only purges, or
// only creates, quietly gaining a PUT that EDITS. The detector there cannot tell a write from a read — its own
// header says so, and fixing that needs the compiler rather than a regex — so the verdict goes on reading as
// current long after the code it described has moved. `RepositoriesController`'s entry said "watch this one",
// which is a note, and a note is not a test.
//
// WHAT THIS DOES INSTEAD. It pins the SHAPE each verdict was written against — how many mutating actions the
// controller had at the time — and fails when that changes. It does not try to judge whether the new action
// edits anything; it says only "this controller is not what it was when somebody decided it was exempt, so go
// and read the decision again". That is a much smaller claim than the detector's, and it is one a regex can
// actually make.
//
// IT WILL FIRE ON UNRELATED ENDPOINTS TOO, and that is the intent rather than a tolerated cost. Adding any
// mutating action to an exempt controller is exactly the moment somebody should re-read why it is exempt. The
// alternative — firing only on actions a heuristic believes are edits — reintroduces the judgement that is
// wrong 9 times in 11 (measured when the untracked-entity guard was calibrated, #1220).
//
// WHEN THIS FAILS: read the verdict named in the message, decide whether it is still true of the controller as
// it now stands, and then update the number here in the same commit. Updating the number WITHOUT re-reading
// the verdict defeats the entire mechanism — it is two seconds of work either way, and only one of them is the
// point.
public partial class ExemptControllerShapeTests
{
    // Controller -> mutating actions when its verdict was last read. Measured, never guessed: the numbers below
    // came from counting, and the test recounts rather than trusting them.
    private static readonly Dictionary<string, int> ShapeWhenJudged = new(StringComparer.Ordinal)
    {
        // Six added with #1270: the detector now sees a controller by its DbSet USE rather than by the
        // entity name appearing anywhere in the file, so these carry a verdict for the first time and
        // need their shape pinned like every other judged controller.
        ["AclEntriesController.cs"] = 3,
        ["AdminController.cs"] = 1,
        ["AuditEventsController.cs"] = 2,
        ["AuthorizationController.cs"] = 1,
        ["BookingsController.cs"] = 2,
        ["CheckoutsController.cs"] = 3,
        ["DocumentAnnotationsController.cs"] = 3,
        ["DocumentBulkController.cs"] = 6,
        ["DocumentChatController.cs"] = 1,
        ["DocumentChildrenController.cs"] = 1,
        ["DocumentExternalLinksController.cs"] = 3,
        ["DocumentLifecycleController.cs"] = 5,
        ["DocumentReferencesController.cs"] = 2,
        ["DocumentRemindersController.cs"] = 2,
        ["DocumentSearchableController.cs"] = 1,
        ["DocumentSubscriptionsController.cs"] = 2,
        ["DocumentTransferController.cs"] = 1,
        ["DocumentVersionsController.cs"] = 4,
        ["DocumentsController.cs"] = 3,
        ["GroupsController.cs"] = 6,
        ["IntrayController.cs"] = 6,
        ["LegalHoldsController.cs"] = 4,
        ["MachineTransitionsController.cs"] = 1,
        ["MasksController.cs"] = 1,
        ["ModulesController.cs"] = 3,
        ["NotebookController.cs"] = 2,
        ["NotificationsController.cs"] = 3,
        ["PasskeysController.cs"] = 3,
        ["PersonalRepositoryController.cs"] = 1,
        ["RecycleBinController.cs"] = 2,
        ["RepositoriesController.cs"] = 4,
        ["RetentionController.cs"] = 2,
        ["SavedSearchesController.cs"] = 3,
        ["SearchablePdfBackfillController.cs"] = 1,
        ["SensitivityLabelsController.cs"] = 4,
        ["TenantsController.cs"] = 1,
        ["TokenController.cs"] = 1,
        ["TypedItemsController.cs"] = 2,
        ["UsersController.cs"] = 12,
        ["WorkflowController.cs"] = 5,
    };

    [GeneratedRegex(@"\[Http(Post|Put|Delete|Patch)")]
    private static partial Regex MutatingAction();

    /// <summary>The controllers named by any verdict in the sibling ratchet, read from that file.</summary>
    [GeneratedRegex(@"\[""([A-Za-z]+Controller\.cs):[A-Za-z]+""\]")]
    private static partial Regex JudgedController();

    [Fact]
    public void A_controller_carrying_a_verdict_has_not_grown_a_new_way_to_mutate()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var ledger = File.ReadAllText(Path.Combine(
            root, "tests", "SimplArchive.UnitTests", "ConcurrencyContractRatchetTests.cs"));

        // Read the judged set from the ledger itself rather than repeating it here. A second hand-kept list
        // would drift from the first, and then this guard would be watching controllers nobody has judged
        // while missing ones somebody has.
        var judged = JudgedController().Matches(ledger)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(judged.Count > 25,
            $"Only {judged.Count} judged controllers found in the ledger — its shape changed and this guard "
            + "stopped seeing them.");

        var controllers = Path.Combine(root, "src", "SimplArchive.Api", "Controllers");
        var missing = judged.Where(n => !ShapeWhenJudged.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0,
            "These controllers carry a verdict but no pinned shape, so a new mutating action on them would go\n"
            + "unnoticed. Count their [HttpPost]/[HttpPut]/[HttpDelete] actions and add them here:\n"
            + string.Join("\n", missing.Select(n => $"  {n}")));

        var stale = ShapeWhenJudged.Keys.Where(n => !judged.Contains(n, StringComparer.Ordinal)).ToList();
        Assert.True(stale.Count == 0,
            "These controllers no longer carry any verdict, so pinning their shape watches nothing. Remove:\n"
            + string.Join("\n", stale.Select(n => $"  {n}")));

        var moved = new List<string>();
        foreach (var name in judged)
        {
            var path = Path.Combine(controllers, name);
            if (!File.Exists(path))
            {
                moved.Add($"  {name} — no longer exists; remove its verdict and its pinned shape");
                continue;
            }

            var now = MutatingAction().Matches(File.ReadAllText(path)).Count;
            if (now != ShapeWhenJudged[name])
            {
                moved.Add($"  {name} — {now} mutating actions now, {ShapeWhenJudged[name]} when its verdict was written");
            }
        }

        Assert.True(moved.Count == 0,
            "These controllers carry a verdict about what they do with a tracked entity, and they have grown or\n"
            + "lost a way to mutate since that verdict was written:\n"
            + string.Join("\n", moved)
            + "\n\nRE-READ the verdict in ConcurrencyContractRatchetTests before touching the number here. An"
            + "\nexemption that was true when written — 'this controller only creates', 'only purges' — stops"
            + "\nbeing true the moment it gains an endpoint that edits, and no other guard can see that: the"
            + "\ndetector there cannot tell a write from a read. Updating the count without re-reading the"
            + "\nverdict is the one way to make this test worse than useless.");
    }
}
