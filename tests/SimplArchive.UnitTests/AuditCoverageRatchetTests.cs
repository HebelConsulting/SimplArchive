using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Which mutating controllers record NOTHING in the audit trail, as a ratchet (issue #1092).
//
// WHY A GUARD AND NOT JUST THE RULE. CLAUDE.md stated that "every user-facing mutation is covered". It was
// not: of 78 controllers, 24 that mutate took no IAuditRecorder at all — including the ones issuing protocol
// credentials and managing platform administrators. A guarantee nothing measures is a hope, and this one was
// load-bearing, because a stated guarantee is exactly what stops the next person checking.
//
// WHY THIS SHAPE. A guard demanding an audit call in every mutating action would have failed on two dozen
// files on the day it landed, and a guard that fails everywhere gets suppressed rather than read. So this
// pins the LIST instead, the same way OverLimitFileCeilingTests and StringEmptyRatchetTests pin numbers: an
// invisible gap becomes a visible debt list that may only shrink, and the next unaudited controller fails the
// build instead of joining the pile quietly.
//
// WHAT IT DOES NOT CLAIM. Holding an IAuditRecorder is not proof that the RIGHT things are recorded — a
// controller could inject one and never call it, or record a create and not a delete. This measures the crude
// thing honestly rather than the precise thing badly, which is the same trade StringEmptyRatchetTests made
// after a hand-rolled classifier got it wrong in both directions.
//
// It also cannot see an audit that lives DEEPER than the controller, which is not hypothetical: bookings are
// recorded at the classifier precisely because that is the one door every write path uses, so the controller
// holds no recorder and is listed below as audited-elsewhere. An entry's reason is therefore load-bearing —
// the list mixes real debt with deliberate design, and only the sentence beside it says which.
//
// WHEN THIS FAILS: a controller that mutates gained no audit. Record its acts, or — if it genuinely records
// nothing a reader of the trail would want — add it below WITH ITS REASON in the same commit. A controller
// that has PAID its debt must be removed from the list in the same commit that audits it.
public partial class AuditCoverageRatchetTests
{
    // Mutating controllers that record nothing, each with why it is acceptable for now. This list may only
    // get shorter.
    private static readonly Dictionary<string, string> UnauditedMutatingControllers = new()
    {
        ["BookingsController.cs"] = "AUDITED ELSEWHERE: at CalendarContactClassifier, the one door every booking write path uses — recording it here instead is what produced the asymmetry #1092 exists to fix.",
        ["AuthorizationController.cs"] = "The OIDC authorize endpoint; the sign-in it results in is recorded as Auth.LoggedIn.",
        ["CheckoutPagesController.cs"] = "Renders the check-out page; the acts it posts to are audited by their own controllers.",
        ["DavCollectionColorController.cs"] = "A per-user calendar colour. A display preference is not an audit event.",
        ["DocumentAppointmentController.cs"] = "DEBT: appointment edits are document writes and should be audited.",
        ["DocumentContactCardController.cs"] = "DEBT: contact edits are document writes and should be audited.",
        ["DocumentItemSourceController.cs"] = "DEBT: rewrites an item's source bytes — a content change, unrecorded.",
        ["DocumentRemindersController.cs"] = "DEBT: a reminder is a commitment about someone's attention.",
        ["DocumentSubscriptionsController.cs"] = "Subscribing to a document is a personal preference, not an act on it.",
        ["IntrayPagesController.cs"] = "Renders the intray page; the filing acts it posts to are audited elsewhere.",
        ["MasksController.cs"] = "DEBT, and the sharpest one left: changing the metadata schema every document is classified against.",
        ["MeController.cs"] = "DEBT: carries profile edits; the credential endpoints beside it are now audited.",
        ["NotificationsController.cs"] = "Marking one's own notifications read is a personal act with no subject but the reader.",
        ["PersonalRepositoryController.cs"] = "DEBT: provisions somebody's personal space.",
        ["PlatformAdministratorsController.cs"] = "DEBT, security-relevant: the most privileged principal type in the system.",
        ["SavedSearchesController.cs"] = "A saved search is a personal convenience, visible to nobody else.",
        ["SearchReindexController.cs"] = "DEBT: an operator action with a large blast radius.",
        ["SearchablePdfBackfillController.cs"] = "DEBT: an operator action that rewrites stored content in bulk.",
        ["SensitivityLabelsController.cs"] = "DEBT: labels drive clearance enforcement.",
        ["TagsController.cs"] = "DEBT: catalogue tags are tenant-wide metadata.",
        ["UserInfoController.cs"] = "The OIDC userinfo endpoint — it returns claims and mutates nothing of consequence.",
    };

    [GeneratedRegex(@"\[Http(Post|Put|Delete|Patch)")]
    private static partial Regex MutatingAction();

    [Fact]
    public void No_new_controller_mutates_without_recording_anything()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var controllers = Directory.GetFiles(
            Path.Combine(root, "src", "SimplArchive.Api", "Controllers"), "*Controller.cs");
        Assert.True(controllers.Length > 50,
            $"Only {controllers.Length} controllers found — the layout changed and this guard stopped seeing them.");

        var unaudited = controllers
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(c => MutatingAction().IsMatch(c.Text) && !c.Text.Contains("IAuditRecorder", StringComparison.Ordinal))
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var appeared = unaudited.Where(n => !UnauditedMutatingControllers.ContainsKey(n)).ToList();
        Assert.True(appeared.Count == 0,
            "These controllers mutate and record nothing in the audit trail, and are not on the accepted list:\n"
            + string.Join("\n", appeared.Select(n => $"  {n}"))
            + "\n\nRecord their acts — or, if they genuinely record nothing a reader of the trail would want,"
            + "\nadd them to UnauditedMutatingControllers WITH THE REASON, in this same commit.");

        // The other direction, which is what makes it a ratchet rather than a list that rots: a controller
        // that has since been audited must leave the list, or the list slowly stops describing anything.
        var paid = UnauditedMutatingControllers.Keys.Where(n => !unaudited.Contains(n)).ToList();
        Assert.True(paid.Count == 0,
            "These controllers now record audit events and must be REMOVED from UnauditedMutatingControllers:\n"
            + string.Join("\n", paid.Select(n => $"  {n}")));
    }
}
