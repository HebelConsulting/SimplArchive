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

    // The DbSet each entity actually lives in. Explicit, because naive pluralisation is WRONG for one of the
    // six and was wrong silently: the detector built `_dbContext.AclEntrys`, which matches nothing, so
    // AclEntry was never checked at all. Three controllers write AclEntries and not one of them was ever
    // flagged for it — the guard reported on five of six entities while reading as though it covered all six.
    //
    // A table beats a rule here for the usual reason: the rule has to be right about every name, and this one
    // was right about five and silently wrong about the sixth.
    private static readonly Dictionary<string, string> DbSetByEntity = new(StringComparer.Ordinal)
    {
        ["Document"] = "Documents",
        ["Tenant"] = "Tenants",
        ["User"] = "Users",
        ["ServiceAccount"] = "ServiceAccounts",
        ["AclEntry"] = "AclEntries",
        ["WorkflowState"] = "WorkflowStates",
    };

    // Pairs where the controller only ever CREATES that entity. A create has no prior version to conflict
    // with, so a precondition on it asserts nothing and cannot fail — it is not debt, and it is not a read
    // either, which is why neither of the other two lists could describe it honestly.
    //
    // Checked from the other side like ReadsOnly: an entry that stops being flagged is removed rather than
    // left standing. It deliberately does NOT go in PermanentlyExempt, which has no staleness check — the day
    // one of these grows an UPDATE it must reappear, not stay hidden.
    private static readonly Dictionary<string, string> CreatesOnly = new(StringComparer.Ordinal)
    {
        ["RepositoriesController.cs:AclEntry"] =
            "Creates only: the owner's grant is added as part of creating the repository, in the same save as "
            + "the document row. Nothing here rewrites an existing entry.",
        ["TenantsController.cs:Tenant"] =
            "Creates only: reads tenants directly, and its single mutation is a POST that delegates to "
            + "ITenantProvisioningService, which does the Tenants.Add. The moment this controller grows a PUT "
            + "it needs the contract, which is why this is not a permanent exemption.",
    };

    // Controllers that should NEVER take a contract, with why. Empty on purpose: nothing has yet been shown to
    // belong here, and guessing would defeat the point of the reason.
    private static readonly Dictionary<string, string> PermanentlyExempt = new();

    // Pairs where the controller only ever READS that entity — it names the DbSet to resolve a name, check
    // existence or list candidates, and writes no column on it. The detector cannot tell a read from a write
    // (see above), so without this the pair could never be paid off and the debt list would quietly become a
    // list of MENTIONS, which is the rot this guard exists to prevent.
    //
    // An entry here is a CLAIM, and it is checked from the other side: if the pair stops being flagged, the
    // claim is removed rather than left standing unverifiable.
    private static readonly Dictionary<string, string> ReadsOnly = new(StringComparer.Ordinal)
    {
        ["DocumentAnnotationsController.cs:Document"] =
            "Reads only: writes DocumentAnnotation rows and projects the document's NAME for its audit lines. "
            + "Like a chat message, an annotation is not an edit of the document, so the token stays put — "
            + "bumping it would refuse the open edit form of whoever is indexing the same document.",
        ["DocumentAnnotationsController.cs:User"] =
            "Reads only: projects an author's DisplayName onto the annotation rows.",
        ["DocumentAnnotationsController.cs:ServiceAccount"] =
            "Reads only: projects an author's Name onto the annotation rows, the service-account half of the "
            + "line above.",
        ["DocumentChatController.cs:ServiceAccount"] =
            "Reads only: projects an author's Name onto the chat rows.",
        ["DocumentSearchableController.cs:Document"] =
            "Reads only: one Name projection for the audit line. The endpoint enqueues an OCR conversion; the "
            + "new version it eventually produces is written by the sidecar pipeline, not here.",
        ["AdminController.cs:Document"] =
            "Reads only: a listing projection of the personal spaces, and one lookup of a space's root to get "
            + "its id for the grant. No document column is written — the take-over writes an ACL ENTRY.",
        ["DocumentChatController.cs:Document"] =
            "Reads only: writes ChatMessage / ChatMessageMention / DocumentSubscription rows and reads the "
            + "document for existence and rights. Deliberately does NOT move the document's token — a comment "
            + "is not an edit of the document, and bumping it would 412 every open edit form in the tenant the "
            + "moment a colleague posted.",
        ["DocumentReferencesController.cs:Document"] =
            "Reads only: writes DocumentReference rows. Placing a reference changes where a document APPEARS, "
            + "not the document, so the parent token stays put.",
        ["DocumentSubscriptionsController.cs:Document"] =
            "Reads only: writes DocumentSubscription rows. Following is PER-USER state; my following a document "
            + "must not invalidate your open edit of it.",
        ["DocumentRemindersController.cs:Document"] =
            "Reads only: writes DocumentReminder rows. Per-user, like a subscription.",
        ["DocumentTagsController.cs:Document"] =
            "Reads only: TagSetWriter writes DocumentTag / TagDefinition rows and the document row is untouched. "
            + "Tags are deliberately the weakest of the metadata (ADR 0796) and are edited from the same pencil, "
            + "so moving the token here would invalidate the very form that just wrote them.",
        ["AuditEventsController.cs:ServiceAccount"] =
            "Reads only: one CanViewAuditLog projection, gating a service-account caller.",
        ["CheckoutsController.cs:Tenant"] =
            "Reads only: one CheckoutTtlDays projection, to date the lock.",
        ["DocumentBulkController.cs:ServiceAccount"] =
            "Reads only: one CanManageRepositories projection, gating the bulk move of a repository root.",
        ["DocumentExternalLinksController.cs:Tenant"] =
            "Reads only: CurrentTenantAsync materialises the tenant to read its external-link policy "
            + "(AllowExternalLinks, ExternalLinkMaxDays, ExternalLinkDefaultAccesses). No property is assigned.",
        ["DocumentVersionsController.cs:ServiceAccount"] =
            "Reads only: one Name projection, naming a version's author.",
        ["DocumentsController.cs:Tenant"] =
            "Reads only: one AnyAsync asking whether the tenant allows external links, gating a rel.",
        ["UsersController.cs:Tenant"] =
            "Reads only: one ImapShowAllDocumentsDefault projection, seeding a NEW user's preference (#793). "
            + "It reads the tenant to write a USER.",
        ["UsersController.cs:ServiceAccount"] =
            "Reads only: projects a calling service account's system-right flags, to gate what it may do.",
        ["RepositoriesController.cs:ServiceAccount"] =
            "Reads only: CanManageRepositories and CanImport projections, gating the caller.",
        ["TokenController.cs:ServiceAccount"] =
            "Reads only: resolves a client_id to { Id, TenantId, IsActive } at login, before the tenant is "
            + "known. A token endpoint authenticates; it writes no principal.",
        ["TokenController.cs:Tenant"] =
            "Reads only: joined to the user solely to read the tenant's Status, so a suspended tenant cannot "
            + "log in.",
        ["WorkflowController.cs:ServiceAccount"] =
            "Reads only: a Name dictionary, labelling who acted in the transition log.",
        ["AclEntriesController.cs:User"] =
            "Reads only: resolves display names for the entry rows, collects tenant-admin ids, and checks a "
            + "principal exists. Projections and AnyAsync — no user column is written.",
        ["AclEntriesController.cs:ServiceAccount"] =
            "Reads only: resolves one service account's name for an audit line, and checks a principal exists. "
            + "No service-account column is written.",
    };

    // The ADR 0795 conversion debt, one entry per (controller, ENTITY) pair. Recording the real number is what
    // makes the next tranche measurable, and what stops the first convenient moment from quietly becoming the
    // new baseline. THIS LIST MAY ONLY GET SHORTER.
    //
    // Pairs, not controllers, because per controller the debt could not be PAID: AclEntriesController mutates
    // AclEntry and Document and merely READS User and ServiceAccount, so converting everything it writes still
    // left it flagged, and an entry that cannot be removed stops meaning "not yet converted".
    private static readonly HashSet<string> NotYetConverted = new(StringComparer.Ordinal)
    {
        "AuthorizationController.cs:User",
        "BookingsController.cs:Document",
        "DocumentAppointmentController.cs:Document",
        "DocumentBulkController.cs:Document",
        "DocumentChatController.cs:User",
        "DocumentChildrenController.cs:Document",
        "DocumentContactCardController.cs:Document",
        "DocumentExternalLinksController.cs:Document",
        "DocumentItemSourceController.cs:Document",
        "DocumentLifecycleController.cs:User",
        "DocumentRemindersController.cs:User",
        "DocumentTransferController.cs:Document",
        "DocumentVersionsController.cs:User",
        "DocumentVersionsController.cs:WorkflowState",
        "DocumentsController.cs:Document",
        "DocumentsController.cs:User",
        "GroupsController.cs:ServiceAccount",
        "GroupsController.cs:User",
        "IntrayController.cs:Document",
        "LegalHoldsController.cs:Document",
        "MachineTransitionsController.cs:Document",
        "MasksController.cs:ServiceAccount",
        "NotebookController.cs:Document",
        "PasskeysController.cs:User",
        "PersonalRepositoryController.cs:Document",
        "RecycleBinController.cs:Document",
        "RepositoriesController.cs:Document",
        "SavedSearchesController.cs:User",
        "TenantsController.cs:Tenant",
        "TokenController.cs:User",
        "TypedItemsController.cs:Document",
        "UsersController.cs:Document",
        "UsersController.cs:WorkflowState",
        "WorkflowController.cs:Document",
        "WorkflowController.cs:User",
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
        Assert.True(
            ContractByEntity.Keys.All(DbSetByEntity.ContainsKey) && DbSetByEntity.Count == ContractByEntity.Count,
            "Every tracked entity needs its DbSet name here, or the detector silently stops checking it — which "
            + "is exactly what naive pluralisation did to AclEntry.");

        Assert.True(controllers.Length > 50,
            $"Only {controllers.Length} controllers found — the layout changed and this guard stopped seeing them.");

        var flagged = controllers
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(c => MutatingAction().IsMatch(c.Text))
            .SelectMany(c => ContractByEntity
                .Where(e => MentionsEntitySet(c.Text, e.Key) && !c.Text.Contains(e.Value, StringComparison.Ordinal))
                .Select(e => $"{c.Name}:{e.Key}"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var appeared = flagged
            .Where(n => !NotYetConverted.Contains(n) && !ReadsOnly.ContainsKey(n)
                && !CreatesOnly.ContainsKey(n) && !PermanentlyExempt.ContainsKey(n))
            .ToList();
        Assert.True(appeared.Count == 0,
            "These controllers mutate a concurrency-tracked entity without going through its verb contract\n"
            + "(ADR 0795), and are on none of the lists:\n"
            + string.Join("\n", appeared.Select(n => $"  {n}"))
            + "\n\nTake the entity's *Verbs contract and mutate through it — or, if this pair only ever READS"
            + "\nthat entity, add it to ReadsOnly WITH THE REASON, in this same commit.");

        // The other direction, which is what makes it a ratchet rather than a list that rots.
        var paid = NotYetConverted.Where(n => !flagged.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(paid.Count == 0,
            "These pairs now use the entity's verb contract and must be REMOVED from NotYetConverted\n"
            + "in the same commit that converted them:\n"
            + string.Join("\n", paid.Select(n => $"  {n}")));

        // A ReadsOnly claim that is no longer flagged is a claim nobody is checking any more — the controller
        // took the contract, or stopped naming the DbSet. Either way the sentence beside it has gone stale.
        var stale = ReadsOnly.Keys.Concat(CreatesOnly.Keys)
            .Where(n => !flagged.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "These ReadsOnly/CreatesOnly entries are no longer flagged, so their stated reason is unverifiable.\n"
            + "Remove them:\n"
            + string.Join("\n", stale.Select(n => $"  {n}")));
    }

    // A whole-word DbSet mention, so `User` does not match `UserId` or `CurrentUserAccessor`, and `Document`
    // does not match `DocumentVersions`. Calibration matters more than reach here: a guard that flags nearly
    // every file is one people suppress rather than read.
    private static bool MentionsEntitySet(string text, string entity) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(entity)}\b(?!\w)")
        && Regex.IsMatch(text, $@"(_dbContext|dbContext)\.{Regex.Escape(DbSetByEntity[entity])}\b");
}
