using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Modules;

/// <summary>
/// Runs a module's populate-on-open hook for a folder that a PROTOCOL surface is enumerating — a WebDAV
/// <c>PROPFIND</c>, an IMAP <c>SELECT</c> (ABI 0.27, ADR 0810, issue #1286).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The hook is invoked by the two SimplArchive clients and by nothing else, while the
/// ephemeral sweep purges expired content on schedule regardless of who is looking. So a user who reaches the
/// archive over a mounted drive or a mail client saw an <b>empty</b> weather folder — not a stale one, not an
/// error, nothing — and no surface on that path could say why.
/// </para>
/// <para>
/// <b>One runner, both protocols.</b> The eligibility question has four parts that must be asked in the same
/// order with the same answers on either surface; two copies of it is how WebDAV and IMAP come to disagree
/// about whether a tenant opted in. The engine, the activation check and the setting are all read here.
/// </para>
/// </remarks>
public sealed class ProtocolReadRefreshRunner(
    SimplArchiveDbContext dbContext,
    StateMachineCatalog machines,
    StateMachineEngine engine,
    ModuleContentHealthRecorder health,
    ICurrentTenantAccessor tenant,
    ILogger<ProtocolReadRefreshRunner> logger)
{
    /// <summary>
    /// Invokes every eligible, enabled hook whose subject is <paramref name="folderId"/>, unless the folder
    /// already holds unexpired staged content. Never throws: a refusal or a failure must not take down the
    /// enumeration the caller actually asked for.
    /// </summary>
    public async Task RefreshAsync(Guid folderId, string surface, CancellationToken cancellationToken)
    {
        // The cheapest gate FIRST, and in memory. On an installation with no module — or with modules whose
        // hooks all declare Never, which is every module written before ABI 0.27 — a PROPFIND must pay nothing
        // at all for this feature. Every DB query below is behind this line.
        if (!machines.Machines.Values.Any(HasEligibleTransition))
        {
            return;
        }

        try
        {
            var subject = await dbContext.Documents
                .Where(d => d.Id == folderId)
                .Join(dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => new { MaskId = (Guid?)v.MaskId, d.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (subject?.MaskId is not { } subjectMask)
            {
                return;
            }

            // The NAME, read here rather than at the failure: a notification saying which folder stopped
            // refreshing is the difference between something an administrator can act on and a module id.
            var folderName = subject.Name;

            // A protocol read always has a resolved tenant — the session authenticated as somebody. Absent is
            // a programming error rather than a caller state, and health recording is the only thing that
            // needs it, so it degrades to "do not record" rather than refusing the populate.
            var tenantId = tenant.TenantId;

            var candidates = machines.Machines.Values
                .Where(m => m.SubjectMaskId == subjectMask && HasEligibleTransition(m))
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // The cooldown, and it is deliberately STATELESS. A WebDAV client enumerates on its own schedule —
            // an indexer, a thumbnailer, a file-manager window somebody left open — so "run the hook on every
            // read" would quietly become the unattended scraper ADR 0756 rejected on legal grounds. Unexpired
            // staged content already present IS the record that a fetch happened recently, so no cooldown table
            // is needed and none can go stale: the content's own ExpiresAt is the clock.
            //
            // Shared with the clients' transition POSTs since ADR 0814 (#1309) — the cooldown is a property
            // of the HOOK, not of each caller, so no surface can route around it.
            if (await StagedContentCooldown.HoldsUnexpiredContentAsync(dbContext, folderId, now, cancellationToken))
            {
                return;
            }

            foreach (var machine in candidates)
            {
                if (machine.ModuleId is not { } moduleId)
                {
                    continue;
                }

                if (!await ModuleActivationCheck.IsActiveAsync(dbContext, moduleId, now, cancellationToken))
                {
                    continue;
                }

                if (!await TenantEnabledAsync(moduleId, cancellationToken))
                {
                    // Warning, not Debug: the caller sees an empty folder and cannot tell a tenant that has not
                    // opted in from a module that is broken (ADR 0626). Name the switch, or the administrator is
                    // left guessing which knob reveals more.
                    logger.LogWarning(
                        "Module {ModuleId} declares a protocol-read refresh for folder {FolderId} but tenant setting "
                        + "{SettingKey} is not enabled, so {Surface} serves whatever is already filed. A tenant "
                        + "administrator enables it under the module's settings.",
                        moduleId, folderId, ProtocolReadRefreshSetting.Key, surface);
                    continue;
                }

                foreach (var (transitionName, _) in machine.Transitions.Where(t => IsEligible(t.Value)))
                {
                    logger.LogDebug(
                        "{Surface} invoking populate hook {MachineId}/{TransitionName} on folder {FolderId}.",
                        surface, machine.MachineId, transitionName, folderId);

                    // PER TRANSITION, not around the whole method. The outer catch below is a backstop for
                    // faults in THIS class; wrapping the hook in it too would attribute our own defects to the
                    // module's source — which is not hypothetical: a LINQ predicate that SQLite could not
                    // translate threw here and was logged as "the module's source failed", a false cause for a
                    // defect that was entirely ours (#1286). It would now also record that against the
                    // module's health, which is the same lie made durable and put in front of a tenant.
                    //
                    // It also keeps one dead source from stopping the others: a second machine over the same
                    // folder still runs.
                    try
                    {
                        var result = await engine.ExecuteTransitionAsync(
                            machine.MachineId, transitionName, folderId, now, cancellationToken);
                        if (!result.Satisfied)
                        {
                            // A refusal is the machine WORKING — a guard said no — so it is not a failure and
                            // must not count toward the health threshold. Counting it would notify an
                            // administrator about a rule doing its job.
                            logger.LogWarning(
                                "Populate hook {MachineId}/{TransitionName} was refused on folder {FolderId} during a "
                                + "{Surface} read, so the folder is served as filed. Trace carries the exchange.",
                                machine.MachineId, transitionName, folderId, surface);
                        }
                        else if (tenantId is { } okTenant)
                        {
                            await health.RecordSuccessAsync(okTenant, moduleId, machine.MachineId, folderId, cancellationToken);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex,
                            "Populate hook {MachineId}/{TransitionName} failed during a {Surface} read of folder "
                            + "{FolderId}; serving what is filed. Trace carries the exchange with the provider.",
                            machine.MachineId, transitionName, surface, folderId);
                        if (tenantId is { } failTenant)
                        {
                            await health.RecordFailureAsync(
                                failTenant, moduleId, machine.MachineId, folderId, folderName,
                                ex.Message, cancellationToken);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backstop for THIS class's own faults — the gate queries, the catalog walk. A populate is an
            // enrichment of somebody else's read, so failing it must degrade to "what is already filed", never
            // to a failed PROPFIND: a mounted drive would appear broken rather than stale.
            //
            // Says "while preparing", not "the module's source failed", because everything reachable from here
            // is ours. The hook's own failures are caught per transition above and attributed to the module.
            logger.LogWarning(ex,
                "Preparing the populate hooks for folder {FolderId} failed during a {Surface} read; serving what "
                + "is filed. This is a fault in the host, not in a module's source.",
                folderId, surface);
        }
    }

    private async Task<bool> TenantEnabledAsync(string moduleId, CancellationToken cancellationToken)
    {
        // Read straight from the store rather than through IModuleArchiveFacade.GetSettingAsync: the facade
        // answers for the CALLING module, and on a protocol read no module is acting yet — this is the host
        // deciding whether one may. The tenant filter still applies (ITenantScoped), and the value is not a
        // secret, so nothing here needs the facade's decryption path.
        var value = await dbContext.ModuleSettingValues
            .Where(s => s.ModuleId == moduleId && s.Key == ProtocolReadRefreshSetting.Key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        // An absent row reads as OFF — the safe direction, and the same one the enum's default takes.
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasEligibleTransition(StateMachineCatalog.MachineDefinition machine) =>
        machine.ModuleId is not null && machine.Transitions.Values.Any(IsEligible);

    private static bool IsEligible(StateMachineCatalog.TransitionDefinition transition) =>
        transition is { AutoRefreshOnOpen: true, ProtocolRead: ProtocolReadRefresh.WhenTenantEnables };
}
