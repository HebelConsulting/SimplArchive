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
        ["Group"] = "GroupVerbs",
        ["TagDefinition"] = "TagDefinitionVerbs",
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
        ["Group"] = "Groups",
        ["TagDefinition"] = "TagDefinitions",
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
        ["DocumentChildrenController.cs:Document"] =
            "Creates only: Documents.Add(child) is its single write, and the file assigns no property on an "
            + "existing document. A create has no prior version to conflict with.",
        ["NotebookController.cs:Document"] =
            "Creates only: Documents.Add(section) is its single write, same shape as the children endpoint.",
        ["TypedItemsController.cs:Document"] =
            "Creates only: two POST actions and nothing else — a contact and an appointment — each adding one "
            + "document. It calls DocumentFinalizer, which DOES update an existing document elsewhere, but here "
            + "the row it updates is the one added moments earlier in the same transaction: there is no prior "
            + "version for a precondition to be about. That distinction is the whole reason this list exists "
            + "separately from ReadsOnly.",
    };

    // Controllers that should NEVER take a contract, with why. An entry here is the OWNER's decision, not the
    // author's — the same rule as the 1000-line limit.
    private static readonly Dictionary<string, string> PermanentlyExempt = new(StringComparer.Ordinal)
    {
        ["DocumentBulkController.cs:Document"] =
            "Owner's decision (2026-09-14): BULK ACTIONS CARRY NO CONCURRENCY CHECK. A bulk request names a SET "
            + "and carries at most ONE If-Match, so honouring it would require that single token to match every "
            + "document in the set — which is not a precondition, it is a coincidence. The alternative shapes "
            + "(a token per id in the body, or a collection-level CTag) were available and were not taken. "
            + "These loops also save PER ITEM deliberately: each skips what it may not touch and answers with a "
            + "per-item outcome report (ADR 0797), which is the contract callers rely on. Do not 'fix' this by "
            + "wrapping the loop in one transaction — that would turn a partial success into a total failure.",
        ["BookingsController.cs:Document"] =
            "Owner's decision (2026-09-15): the document is the BOOKING's artefact, and the booking carries the "
            + "precondition. Cancel binds If-Match to the booking row and soft-deletes the booking's document "
            + "in the same save, so the action is already gated — by the token the client actually holds, "
            + "because the booking is the resource it asked to cancel. A document If-Match would demand a tag "
            + "it was never given. Creation is a create. RETIRE THIS if an endpoint here ever edits a document "
            + "a user opened, rather than one this controller owns.",
        ["PersonalRepositoryController.cs:Document"] =
            "Owner's decision (2026-09-15): machine-owned maintenance. Every document write is "
            + "PersonalRepositoryProvisioner.EnsureAsync, whose four branches are idempotent HEALS — rename a "
            + "legacy-named folder to the current name, stamp a mask on a maskless one, restamp one wearing a "
            + "superseded mask, or create it. They run at login and provisioning, so nobody has a form open and "
            + "there is no tag anyone saw; a precondition here could not fail, and if it could it would make "
            + "the heal itself fail for whoever's token had moved. RETIRE THIS if the controller gains a "
            + "user-facing edit of a personal space.",
        ["UsersController.cs:Document"] =
            "Owner's decision (2026-09-15): the same provisioning heal as the line above, reached from the "
            + "admin side — this controller's ONLY document write is PersonalRepositoryProvisioner.EnsureAsync, "
            + "and its own use of the Documents set is one join. Its User writes go through UserVerbs, which is "
            + "the pair that matters here and is already converted.",
        ["MachineTransitionsController.cs:Document"] =
            "Owner's decision (2026-09-15): a login-less module service principal acts, not a user. The writes "
            + "happen in StateMachineEngine → ModuleArchiveFacade, on the module's own schedule, and ADR 0737 "
            + "already gives the engine its transaction. There is no user and therefore no user's tag — a "
            + "precondition would be a guard that cannot fail. RETIRE THIS if a transition ever becomes "
            + "something a user commits from a form carrying an ETag.",
        ["RecycleBinController.cs:Document"] =
            "Owner's decision (2026-09-15): a purge is a DELETE, not an edit. Its single write is "
            + "DocumentPurger's Documents.RemoveRange — there is no post-state for a precondition to protect, "
            + "and the row it would be checked against is the row being removed. The caller has already chosen "
            + "the subtree explicitly. See #1172, which asked this question for the purge path.",
        ["RepositoriesController.cs:Document"] =
            "Owner's decision (2026-09-15): creates, a purge, and a bulk import — no user edit among them. The "
            + "controller has NO SaveChanges of its own: it adds a repository and a subfolder, and delegates "
            + "the rest to DocumentPurger (a hard delete, as above) and RepositoryImporter (below). WATCHED BY ExemptControllerShapeTests (#1224), which fails the build if this controller grows or loses a mutating action so the verdict gets re-read rather than assumed. WATCH THIS "
            + "ONE: it is the largest of the seven and the likeliest to grow a real per-document edit, which "
            + "this list would not catch — PermanentlyExempt has no staleness check.",
        ["DocumentTransferController.cs:Document"] =
            "Owner's decision (2026-09-15): an import is a bulk SYNC, not an edit. RepositoryImporter matches "
            + "documents by ORIGIN (OriginTenantId + OriginDocumentId) and, when updateExisting is set, takes "
            + "the archive's name for a matched row. The caller holds an archive, not a document it read — "
            + "there is no per-document tag it could send, and one token could not speak for a whole import "
            + "(the same reason bulk actions carry none, see DocumentBulkController above).",
    };

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
        ["AuditEventsController.cs:ServiceAccount"] =
            "Reads only: one CanViewAuditLog projection, gating a service-account caller.",
        ["CheckoutsController.cs:Tenant"] =
            "Reads only: one CheckoutTtlDays projection, to date the lock.",
        ["DocumentBulkController.cs:ServiceAccount"] =
            "Reads only: one CanManageRepositories projection, gating the bulk move of a repository root.",
        ["DocumentExternalLinksController.cs:Tenant"] =
            "Reads only: CurrentTenantAsync materialises the tenant to read its external-link policy "
            + "(AllowExternalLinks, ExternalLinkMaxDays, ExternalLinkDefaultAccesses). No property is assigned.",
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
        ["DocumentLifecycleController.cs:User"] =
            "Reads only: one DisplayName projection, naming the holder in the 'checked out by …' refusal.",
        ["LegalHoldsController.cs:Document"] =
            "Reads only: writes LegalHold / LegalHoldItem rows, and materialises the document purely to name "
            + "it in the audit line. Placing a hold FREEZES a document rather than editing it, so the token "
            + "stays put. IWormLockService.ReconcileAsync applies the lock in object storage and only READS "
            + "the document row.",
        ["DocumentExternalLinksController.cs:Document"] =
            "Reads only: writes ExternalLink rows — and carries the precondition on the LINK, which is the "
            + "entity a caller is actually editing. The document is read for existence, rights and its name.",
        ["DocumentVersionsController.cs:WorkflowState"] =
            "Reads only: reads the state to answer whether a version is workflow-GATED, and to project a "
            + "status onto the listing. Every write it makes is to the document and its versions, which go "
            + "through DocumentVerbs.",
        ["UsersController.cs:WorkflowState"] =
            "Reads only: one join, projecting a user's open workflow load onto the listing.",
        ["DocumentsController.cs:User"] =
            "Reads only: projects a check-out holder's DisplayName and the candidate reviewers. No injected "
            + "service touches the Users set at all.",
        ["WorkflowController.cs:Document"] =
            "Reads only: projects the document's name, its check-out holder and its mask's SLA to run the "
            + "transition. The state it writes goes through WorkflowStateVerbs — which is the point: a "
            + "workflow transition is an edit of the STATE, not of the document, so the document's token "
            + "stays put and an open edit form elsewhere survives it.",
        ["AuthorizationController.cs:User"] =
            "Reads only: one projection of { TenantId, Email, IsActive } at the authorization endpoint, with "
            + "the tenant filter ignored because the interim cookie carries no tenant_id yet. Login resolves a "
            + "principal; it writes none.",
        ["PasskeysController.cs:User"] =
            "Reads only: loads the user to associate a WebAuthn credential with. The writes are "
            + "WebAuthnCredential rows; no user column is touched.",
        ["TokenController.cs:User"] =
            "Reads only: resolves the subject at login, and the ACTOR and TARGET of an impersonation exchange "
            + "(RFC 8693) to check both are active. A token endpoint authenticates; it writes no principal.",
        ["GroupsController.cs:User"] =
            "Reads only: an AnyAsync existence check before adding a membership, a DisplayName projection for "
            + "the rows, and a join listing a group's members. Memberships are their own entity.",
        ["GroupsController.cs:ServiceAccount"] =
            "Reads only: resolves a service account for the membership rows and the caller's rights.",
        ["SavedSearchesController.cs:User"] =
            "Reads only: OwnerName projections, and a check that share targets are ACTIVE before the share "
            + "rows are written. The writes are SavedSearch and its share rows.",
        ["DocumentChatController.cs:User"] =
            "Reads only: DisplayName projections for authors and mentions, plus the active-user list the "
            + "mention picker offers.",
        ["DocumentRemindersController.cs:User"] =
            "Reads only: TargetName and CreatedByName projections, and resolving the reminder's targets.",
        ["WorkflowController.cs:User"] =
            "Reads only: resolves the reviewer being assigned and a DisplayName dictionary labelling the "
            + "transition log. The workflow state is a separate entity, already on this list.",
        ["MasksController.cs:ServiceAccount"] =
            "Reads only: one lookup gating the caller. (The WellKnownMaskSeeder reference in this file is a "
            + "static field name, not a service that writes.)",
        ["DocumentSubscriptionsController.cs:Document"] =
            "Reads only (owner's decision, 2026-09-15): writes the CALLER'S OWN subscription row. Two people "
            + "cannot collide on it — each writes their own — and moving the document's token because somebody "
            + "FOLLOWED it would 412 every open edit form in the tenant. Same family as a chat message: "
            + "following a document is not an edit of it.",
    };

    // The ADR 0795 conversion debt, one entry per (controller, ENTITY) pair. Recording the real number is what
    // makes the next tranche measurable, and what stops the first convenient moment from quietly becoming the
    // new baseline. THIS LIST MAY ONLY GET SHORTER.
    //
    // IT IS NOW EMPTY (2026-09-15), which changes what this guard does rather than retiring it: every pair is
    // either converted, or classified with a reason on one of the three lists above. A newly-flagged pair now
    // fails the build the moment it appears, instead of being absorbed into a backlog — which is the whole
    // point of a ratchet, and only true once the backlog is gone.
    //
    // Do NOT re-open it to park something. An entry added here now would be the first, with nothing else
    // beside it to make it look temporary, and that is precisely how 69 accumulated the first time.
    //
    // Pairs, not controllers, because per controller the debt could not be PAID: AclEntriesController mutates
    // AclEntry and Document and merely READS User and ServiceAccount, so converting everything it writes still
    // left it flagged, and an entry that cannot be removed stops meaning "not yet converted".
    private static readonly HashSet<string> NotYetConverted = new(StringComparer.Ordinal)
    {
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

        // A claim that is no longer flagged is a claim nobody is checking any more — the controller took the
        // contract, or stopped naming the DbSet. Either way the sentence beside it has gone stale.
        //
        // PermanentlyExempt is included, and it did NOT used to be. That mattered once the debt list emptied:
        // the exemptions became the only content, and an exemption nobody checks is indistinguishable from a
        // forgotten one. BE CLEAR ABOUT WHAT THIS DOES AND DOES NOT CATCH — it catches an exemption whose
        // controller no longer exists, was renamed, or stopped touching the entity. It CANNOT catch the case
        // that actually worries: a controller growing a genuine per-document user edit alongside the writes
        // that were exempted, because the detector cannot tell a write from a read (see the header). That is
        // why RepositoriesController's entry says to watch it rather than pretending a test will.
        var stale = ReadsOnly.Keys.Concat(CreatesOnly.Keys).Concat(PermanentlyExempt.Keys)
            .Where(n => !flagged.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "These ReadsOnly/CreatesOnly/PermanentlyExempt entries are no longer flagged, so their stated\n"
            + "reason is unverifiable. Remove them:\n"
            + string.Join("\n", stale.Select(n => $"  {n}")));
    }

    // The four lists must be DISJOINT. Nothing enforced that, and it silently mattered: TenantsController.cs
    // :Tenant was filed in CreatesOnly with its reason AND left standing in NotYetConverted, so the debt read
    // one higher than it was and a pair carried two different verdicts at once. Neither existing check could
    // see it — the exclusion in the flagged-pairs query simply skips a pair twice, and the staleness check
    // asks whether a claim is still FLAGGED, which a pair in both lists is.
    //
    // The direction of the error is what makes this worth a test rather than a tidy-up: a duplicate inflates
    // the debt, so the ledger reports work that is already done as still owed. A burn-down measured against a
    // number that lies is the specific failure this whole file exists to prevent.
    [Fact]
    public void A_pair_is_claimed_in_exactly_one_list()
    {
        var lists = new (string Name, IEnumerable<string> Keys)[]
        {
            ("NotYetConverted", NotYetConverted),
            ("ReadsOnly", ReadsOnly.Keys),
            ("CreatesOnly", CreatesOnly.Keys),
            ("PermanentlyExempt", PermanentlyExempt.Keys),
        };

        var duplicates = lists
            .SelectMany(l => l.Keys.Select(k => (Pair: k, List: l.Name)))
            .GroupBy(x => x.Pair, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"  {g.Key} — claimed in {string.Join(" and ", g.Select(x => x.List).OrderBy(n => n, StringComparer.Ordinal))}")
            .ToList();

        Assert.True(duplicates.Count == 0,
            "A (controller, entity) pair carries ONE verdict. These carry more than one, so the debt count is "
            + "wrong and at least one stated reason is unenforced:\n"
            + string.Join("\n", duplicates));
    }

    // A whole-word DbSet mention, so `User` does not match `UserId` or `CurrentUserAccessor`, and `Document`
    // does not match `DocumentVersions`. Calibration matters more than reach here: a guard that flags nearly
    // every file is one people suppress rather than read.
    private static bool MentionsEntitySet(string text, string entity) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(entity)}\b(?!\w)")
        && Regex.IsMatch(text, $@"(_dbContext|dbContext)\.{Regex.Escape(DbSetByEntity[entity])}\b");
}
