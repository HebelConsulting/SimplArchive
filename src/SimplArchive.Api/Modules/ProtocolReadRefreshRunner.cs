using Microsoft.EntityFrameworkCore;
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
            var maskId = await dbContext.Documents
                .Where(d => d.Id == folderId)
                .Join(dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => (Guid?)v.MaskId)
                .FirstOrDefaultAsync(cancellationToken);
            if (maskId is not { } subjectMask)
            {
                return;
            }

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
            // The null-check is the SQL predicate and the COMPARISON is in memory, exactly as the sweep worker
            // that purges the same rows does it: SQLite cannot compare a DateTimeOffset in SQL, and the model
            // runs on both providers (ADR 0002's parity rule). Written as one predicate it throws at QUERY
            // time — which this runner's own catch would then have reported as "the module's source failed",
            // a false cause for a defect that is entirely ours.
            var expiries = await dbContext.Documents
                .Where(d => d.ParentId == folderId && d.ExpiresAt != null)
                .Select(d => d.ExpiresAt)
                .ToListAsync(cancellationToken);
            if (expiries.Any(e => e > now))
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
                    var result = await engine.ExecuteTransitionAsync(
                        machine.MachineId, transitionName, folderId, now, cancellationToken);
                    if (!result.Satisfied)
                    {
                        logger.LogWarning(
                            "Populate hook {MachineId}/{TransitionName} was refused on folder {FolderId} during a "
                            + "{Surface} read, so the folder is served as filed. Trace carries the exchange.",
                            machine.MachineId, transitionName, folderId, surface);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A populate is an ENRICHMENT of somebody else's read. Failing it must degrade to "what is already
            // filed", never to a failed PROPFIND — the mounted drive would appear broken rather than stale.
            logger.LogWarning(ex,
                "A populate hook failed during a {Surface} read of folder {FolderId}; serving what is filed. "
                + "Trace carries the exchange with the module's source.",
                surface, folderId);
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
