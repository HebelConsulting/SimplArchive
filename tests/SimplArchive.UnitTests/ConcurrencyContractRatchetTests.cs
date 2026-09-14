using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// ADR 0795 says every concurrency-tracked entity has a NAMED verb contract, and that a mutation which bypasses
// it must fail the build. This is that guard, in the shape AuditCoverageRatchetTests established.
//
// It exists because the alternative was tried and lost. ConcurrencyHeaders (#1166) made the IMPLEMENTATION
// single — one helper where three controllers had copied the same parser under three names — and that changed
// nothing about whether anyone CALLED it: DocumentMetadataController kept SetETag and TryParseETag defined and
// invoked from nowhere, across six mutations of a tracked entity, until #1167. A helper you forget is
// invisible; a controller that does not take `DocumentVerbs` is not.
//
// WHAT IT MEASURES, stated honestly because a guard that overclaims gets suppressed rather than read: a
// controller has mutating actions AND names a tracked entity's DbSet AND does not take that entity's verb
// contract. It is COARSE — it cannot tell a write from a read of that DbSet, and it cannot see whether a
// converted controller uses the contract on every path. That needs the compiler, not a regex. So it catches
// the class of defect that actually occurred (a whole controller wired to nothing) and not a single missed
// call inside a converted one. Measuring the coarse thing honestly beats measuring the precise thing badly.
//
// WHY ONE FLAT LIST rather than a reason per entry, unlike AuditCoverageRatchetTests: there, the list mixed
// real debt with deliberate design and only the sentence beside each said which. Here every entry means the
// same thing — NOT YET CONVERTED — so 47 bespoke justifications would be 47 inventions dressed as findings.
// When an entry is identified as one that should NEVER convert, it moves to PermanentlyExempt with its reason.
//
// WHEN THIS FAILS: a controller mutating a tracked entity appeared without its verb contract. Convert it and
// remove it from the list in the same commit — or, if it genuinely must never convert, move it to
// PermanentlyExempt WITH ITS REASON. A controller that has PAID its debt must leave, or the list stops
// describing anything.
public partial class ConcurrencyContractRatchetTests
{
    // The entities that carry a token today, and the contract each mutation of them goes through (ADR 0795).
    private static readonly Dictionary<string, string> ContractByEntity = new()
    {
        ["Document"] = "DocumentVerbs",
        ["Tenant"] = "TenantVerbs",
        ["User"] = "UserVerbs",
        ["ServiceAccount"] = "ServiceAccountVerbs",
        ["AclEntry"] = "AclEntryVerbs",
        ["WorkflowState"] = "WorkflowStateVerbs",
    };

    // Controllers that should NEVER take a contract, with why. Empty on purpose: nothing has yet been shown to
    // belong here, and guessing would defeat the point of the reason.
    private static readonly Dictionary<string, string> PermanentlyExempt = new();

    // The ADR 0795 conversion debt — every controller the coarse detector above flags today. It starts at 47
    // deliberately: recording the real number is what makes the next tranche measurable, and what stops the
    // first convenient moment from quietly becoming the new baseline. THIS LIST MAY ONLY GET SHORTER.
    private static readonly HashSet<string> NotYetConverted = new(StringComparer.Ordinal)
    {
        "AclEntriesController.cs",
        "AdminController.cs",
        "AuditEventsController.cs",
        "AuthorizationController.cs",
        "BookingsController.cs",
        "CheckoutsController.cs",
        "DocumentAnnotationsController.cs",
        "DocumentAppointmentController.cs",
        "DocumentBulkController.cs",
        "DocumentChatController.cs",
        "DocumentChildrenController.cs",
        "DocumentContactCardController.cs",
        "DocumentExternalLinksController.cs",
        "DocumentItemSourceController.cs",
        "DocumentLifecycleController.cs",
        "DocumentMetadataController.cs",
        "DocumentOriginController.cs",
        "DocumentReferencesController.cs",
        "DocumentRemindersController.cs",
        "DocumentSearchableController.cs",
        "DocumentSubscriptionsController.cs",
        "DocumentTagsController.cs",
        "DocumentTransferController.cs",
        "DocumentVersionsController.cs",
        "DocumentsController.cs",
        "GroupsController.cs",
        "ImapAccessController.cs",
        "IntrayController.cs",
        "LegalHoldsController.cs",
        "MachineTransitionsController.cs",
        "MasksController.cs",
        "MeController.cs",
        "NotebookController.cs",
        "PasskeysController.cs",
        "PersonalRepositoryController.cs",
        "RecycleBinController.cs",
        "RepositoriesController.cs",
        "RetentionController.cs",
        "SavedSearchesController.cs",
        "TenantSettingsController.cs",
        "TenantsController.cs",
        "TokenController.cs",
        "TypedItemsController.cs",
        "UserMfaController.cs",
        "UsersController.cs",
        "WebDavAccessController.cs",
        "WorkflowController.cs",
    };

    [GeneratedRegex(@"\[Http(Post|Put|Delete|Patch)")]
    private static partial Regex MutatingAction();

    [Fact]
    public void No_controller_mutates_a_tracked_entity_without_its_verb_contract()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var controllers = Directory.GetFiles(
            Path.Combine(root, "src", "SimplArchive.Api", "Controllers"), "*Controller.cs");
        Assert.True(controllers.Length > 50,
            $"Only {controllers.Length} controllers found — the layout changed and this guard stopped seeing them.");

        var flagged = controllers
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(c => MutatingAction().IsMatch(c.Text))
            .Where(c => ContractByEntity.Any(e =>
                MentionsEntitySet(c.Text, e.Key) && !c.Text.Contains(e.Value, StringComparison.Ordinal)))
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var appeared = flagged
            .Where(n => !NotYetConverted.Contains(n) && !PermanentlyExempt.ContainsKey(n))
            .ToList();
        Assert.True(appeared.Count == 0,
            "These controllers mutate a concurrency-tracked entity without going through its verb contract\n"
            + "(ADR 0795), and are on neither list:\n"
            + string.Join("\n", appeared.Select(n => $"  {n}"))
            + "\n\nTake the entity's *Verbs contract and mutate through it — or, if this controller must never"
            + "\nconvert, add it to PermanentlyExempt WITH THE REASON, in this same commit.");

        // The other direction, which is what makes it a ratchet rather than a list that rots.
        var paid = NotYetConverted.Where(n => !flagged.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(paid.Count == 0,
            "These controllers now use their entity's verb contract and must be REMOVED from NotYetConverted\n"
            + "in the same commit that converted them:\n"
            + string.Join("\n", paid.Select(n => $"  {n}")));
    }

    // A whole-word DbSet mention, so `User` does not match `UserId` or `CurrentUserAccessor`, and `Document`
    // does not match `DocumentVersions`. Calibration matters more than reach here: a guard that flags nearly
    // every file is one people suppress rather than read.
    private static bool MentionsEntitySet(string text, string entity) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(entity)}\b(?!\w)")
        && Regex.IsMatch(text, $@"(_dbContext|dbContext)\.{Regex.Escape(entity)}s\b");
}
