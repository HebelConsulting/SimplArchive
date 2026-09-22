using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Services that WRITE a concurrency-tracked entity, and why each needs no verb contract of its own (#1210).
//
// WHY THIS EXISTS. `ConcurrencyContractRatchetTests` enumerates `Controllers/*Controller.cs` and nothing else.
// That is accurate about controllers and SILENT about everything else — and the silence read as coverage: the
// debt list was driven to zero and reported as "every tracked mutation goes through its contract", while
// twenty-eight services wrote tracked entities that no guard had ever looked at.
//
// The failure mode is specific and this epic has already produced it once. `RecycleBinController` looked
// read-only to a heuristic because it has no writes of its own — it destroys documents *through a service*.
// A guard that stops at the controller boundary cannot see that, and a green run from it means less than it
// appears to.
//
// WHY THE REASONS ARE SHARED CONSTANTS, not one sentence per service. Twenty-eight bespoke justifications
// written in one sitting would be twenty-eight claims of the quality that produced "DocumentVersion is
// append-only" — a sentence written from an entity's design intent rather than its call sites, which was
// false, and which would have made a guard go green over two unprotected endpoints. Four categories with one
// carefully-argued reason each is both shorter and more honest: the claim is made once, and each service is
// merely ASSIGNED to it, which is a fact anybody can check by reading that service's callers.
//
// WHAT IT MEASURES: a non-controller file that adds or removes a tracked entity, or assigns one of its
// identity/lifecycle columns, and saves. Coarse in the same way its sibling is — it cannot tell whether a
// caller holds a transaction, and it cannot see a write reached through two more layers of indirection.
public partial class ServiceWriteScopeTests
{
    private const string Provisioning =
        "PROVISIONING / SEED / HEAL. Runs at startup or when an account is first reached, with no HTTP caller "
        + "holding an ETag — there is no tag for a precondition to compare, and the writes are idempotent by "
        + "construction, so a re-run converges rather than clobbering. A precondition here could not fail, and "
        + "if it could it would break the heal for whoever's token had moved.";

    private const string Background =
        "BACKGROUND WORKER. Runs on a timer with no caller at all. Its writes are sweeps over whatever matches "
        + "at that moment, saved per item deliberately: one transaction would turn a partial sweep into a total "
        + "failure, and the next run is how it recovers.";

    private const string Protocol =
        "PROTOCOL WRITER. Reached from IMAP / LMTP / WebDAV / CalDAV rather than from the API, where the "
        + "caller's concurrency contract is the protocol's own (an IMAP UIDVALIDITY, a DAV ETag) rather than "
        + "ours. Its FILING path goes through DocumentFinalizer.FileAsync, which owns a transaction and now "
        + "REFUSES to run outside one (#1171).";

    private const string CalleeOfAContract =
        "CALLED INSIDE THE CALLER'S CONTRACT. It has no HTTP request of its own; the controller that invokes it "
        + "states the precondition and owns the transaction. Giving this a second one would put TWO "
        + "preconditions on one user action, which ADR 0794 forbids.";

    private const string BulkByDecision =
        "BULK, owner's decision (2026-09-14): a request naming a SET carries at most one If-Match, so honouring "
        + "it would require that single token to match every row — not a precondition, a coincidence. These "
        + "also save per item on purpose, so a partial result stays partial rather than becoming a total "
        + "failure.";

    // The assignment of each service to one of the reasons above. An entry is a claim about WHO CALLS IT, which
    // is checkable in one grep — deliberately a smaller claim than "this service is safe".
    private static readonly Dictionary<string, string> ServiceVerdicts = new(StringComparer.Ordinal)
    {
        ["DemoDataSeeder.cs"] = Provisioning,
        ["DemoArtistsSeeder.cs"] = Provisioning,
        ["CryptoDemoSeeder.cs"] = Provisioning,
        ["TenantProvisioningService.cs"] = Provisioning,
        ["WellKnownMaskSeeder.cs"] = Provisioning,
        ["PersonalRepositoryProvisioner.cs"] = Provisioning,
        ["PersonalMailboxProvisioner.cs"] = Provisioning,
        ["ArchiveIdentityMapper.cs"] = Provisioning,
        ["ModulePrincipal.cs"] = Provisioning,

        ["EphemeralMailSweepWorker.cs"] = Background,
        ["EphemeralContentSweepWorker.cs"] = Background,
        ["RetentionService.cs"] = Background,

        ["DavWrites.cs"] = Protocol,
        ["WebDavWrites.cs"] = Protocol,
        ["WebDavMoveCopy.cs"] = Protocol,
        ["WebDavSpecialHandlers.cs"] = Protocol,
        ["ImapWrites.cs"] = Protocol,
        ["ImapMailboxLifecycle.cs"] = Protocol,
        ["LmtpDelivery.cs"] = Protocol,

        ["DocumentFinalizer.cs"] = CalleeOfAContract,
        ["CalendarContactClassifier.cs"] = CalleeOfAContract,
        ["ResourceCollectionWriter.cs"] = CalleeOfAContract,
        ["NoteComposer.cs"] = CalleeOfAContract,
        ["TypedItemWriter.cs"] = CalleeOfAContract,
        ["ModuleActivationService.cs"] = CalleeOfAContract,

        ["DocumentPurger.cs"] = BulkByDecision,
        ["DocumentRestorer.cs"] = BulkByDecision,
        ["RepositoryImporter.cs"] = BulkByDecision,

        // NOT settled, and deliberately not dressed as though it were. The module path is safe only because
        // StateMachineEngine opens a transaction (ADR 0737) and every module test arrives through a transition;
        // whether a module can reach this facade's filing path WITHOUT one has not been traced. #1223 carries
        // that question. Listed here so the guard does not flag it daily, with the doubt written down rather
        // than laundered into a verdict.
        ["ModuleArchiveFacade.cs"] = "UNVERIFIED — see #1223. " + CalleeOfAContract,
    };

    [GeneratedRegex(@"\.(Documents|Tenants|Users|ServiceAccounts|AclEntries|WorkflowStates|Groups|TagDefinitions)\.(Add|AddRange|Remove|RemoveRange)\(")]
    private static partial Regex TrackedWrite();

    [GeneratedRegex(@"^\s+[a-z][A-Za-z]*\.(DeletedAt|MaskVersionId|ParentId|CurrentVersionId|SensitivityLabelId|IsActive|Status)\s*=\s*[^=]", RegexOptions.Multiline)]
    private static partial Regex LifecycleAssignment();

    [GeneratedRegex(@"SaveChangesAsync|SaveTranslatingContainmentAsync")]
    private static partial Regex Saves();

    [Fact]
    public void Every_service_that_writes_a_tracked_entity_is_accounted_for()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var src = Path.Combine(root, "src");
        var writers = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains("Migrations", StringComparison.Ordinal))
            .Where(f => !Path.GetFileName(f).EndsWith("Controller.cs", StringComparison.Ordinal))
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .Where(f => Saves().IsMatch(f.Text) && (TrackedWrite().IsMatch(f.Text) || LifecycleAssignment().IsMatch(f.Text)))
            .Select(f => f.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(writers.Count > 20,
            $"Only {writers.Count} service writers found — the layout changed and this guard stopped seeing them.");

        var unaccounted = writers.Where(n => !ServiceVerdicts.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unaccounted.Count == 0,
            "These services write a concurrency-tracked entity and no verdict accounts for them:\n"
            + string.Join("\n", unaccounted.Select(n => $"  {n}"))
            + "\n\nRead the service's CALLERS and assign it to one of the reasons at the top of this file — or,"
            + "\nif it is genuinely a user action writing a tracked entity on its own, give the controller that"
            + "\nreaches it the entity's verb contract (ADR 0795) instead.");

        // The other direction: a verdict about a service that no longer writes anything is a claim nobody is
        // checking. Same rule as the sibling guard's ReadsOnly, for the same reason.
        var stale = ServiceVerdicts.Keys.Where(n => !writers.Contains(n, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "These services no longer write a tracked entity, so their verdict is unverifiable. Remove them:\n"
            + string.Join("\n", stale.Select(n => $"  {n}")));
    }
}
