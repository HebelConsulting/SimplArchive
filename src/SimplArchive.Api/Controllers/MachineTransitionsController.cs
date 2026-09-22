using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Modules;
using SimplArchive.Api.Modules;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Executes a state-machine transition on its subject document (ADRs 0737/0742/0743) — the core-owned
/// endpoint behind every machine's labeled action links, module and (future) core machines alike.
/// </summary>
/// <remarks>
/// <para>
/// The core's gates run first: the caller needs <c>CanEditContent</c> on the subject (a transition's
/// handler mutates the subject's world — owner-decided 2026-09-04), the subject must wear the machine's
/// subject mask, and the machine's declaring module must be ACTIVE for the tenant (404
/// <c>MODULE_NOT_ACTIVE</c> otherwise, exactly like the module's own routes). Only then is the machine
/// consulted; a red guard answers 409 <c>MACHINE_TRANSITION_REFUSED</c> carrying the ADR 0742 diagnosis.
/// </para>
/// <para>
/// The engine owns the handler's transaction: a handler that throws rolls the act back and its exception
/// surfaces (a module's <c>ModuleApiException</c> as its own RFC 7807 problem).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/machine/{machineId}/transitions")]
[Authorize]
public class MachineTransitionsController : ControllerBase
{
    /// <summary>
    /// Rides on every auto-refresh response (ADR 0814): <c>ran</c> = the hook executed and the folder's
    /// contents may have changed; <c>current</c> = unexpired staged content already present, nothing ran.
    /// Clients reload the folder only on <c>ran</c> — both spellings are SUCCESS, and a client that ignores
    /// the header (or an old server that never sends it) safely degrades to reloading every time.
    /// </summary>
    public const string PopulateOutcomeHeader = "X-Populate-Outcome";

    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly StateMachineCatalog _catalog;
    private readonly StateMachineEngine _engine;
    private readonly IAuditRecorder _audit;
    private readonly ModuleContentHealthRecorder _health;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public MachineTransitionsController(
        SimplArchiveDbContext dbContext, DocumentAccessService access, StateMachineCatalog catalog,
        StateMachineEngine engine, IAuditRecorder audit,
        SimplArchive.Api.Modules.ModuleContentHealthRecorder health,
        ICurrentTenantAccessor currentTenantAccessor)
    {
        _dbContext = dbContext;
        _access = access;
        _catalog = catalog;
        _engine = engine;
        _audit = audit;
        _health = health;
        _currentTenantAccessor = currentTenantAccessor;
    }

    [HttpPost("{transitionName}")]
    public async Task<IActionResult> Execute(Guid documentId, string machineId, string transitionName, CancellationToken cancellationToken)
    {
        if (!_catalog.Machines.TryGetValue(machineId, out var machine)
            || !machine.Transitions.ContainsKey(transitionName))
        {
            return NotFound();
        }

        // The declaring module must be active here — the same absence semantics as its own routes
        // (ADR 0543): for an unlicensed tenant this machine does not exist.
        if (machine.ModuleId is { } moduleId
            && !await ModuleActivationCheck.IsActiveAsync(_dbContext, moduleId, DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new ModuleNotActiveException(moduleId);
        }

        // The subject must exist, wear the machine's subject mask, and be WRITABLE by the caller — a
        // handler mutates the subject's world, which is what CanEditContent already means everywhere.
        var subjectMaskId = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => (Guid?)v.MaskId)
            .FirstOrDefaultAsync(cancellationToken);
        if (subjectMaskId != machine.SubjectMaskId)
        {
            return NotFound();
        }

        // A handler mutates the subject's world, which is what CanEditContent means everywhere — EXCEPT the
        // populate hook (ADR 0764): an auto-refresh is an AUTOMATED act the viewer merely triggers, the module
        // principal does the writing under its own consent grants, so being allowed to SEE the subject is the
        // whole ask. (CanSee is already established: an invisible document returned NotFound above via the
        // rights walk in GetCallerRightsAsync — all-false rights read as not-found-shaped Forbid below.)
        var autoRefresh = machine.Transitions[transitionName].AutoRefreshOnOpen;
        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        var required = autoRefresh ? rights.CanSee : rights.CanEditContent;
        if (!required)
        {
            return Forbid();
        }

        // The populate hook's cooldown, on THIS path too (ADR 0814, #1309): a folder that already holds
        // unexpired staged content is current, so opening it ten times in a minute must not issue ten
        // upstream fetches — the hook owns its cooldown, and no caller can route around it by picking a
        // different surface. ADR 0810 built the same gate for the protocol path; this is the one other door.
        // Deliberate transitions are untouched — this branch exists only for the auto-invoked hook.
        //
        // The outcome header is load-bearing, not telemetry: it is what lets a client reload the folder only
        // when content actually changed, and it is the loop brake that replaced the clients' own 30-second
        // timestamps (run → reload → re-trigger → now current → no reload → stop). "current" answers as
        // SUCCESS deliberately — the hook's contract is "make this folder current", and it already is.
        if (autoRefresh)
        {
            var now = DateTimeOffset.UtcNow;
            var interval = machine.Transitions[transitionName].MinimumRefreshInterval;
            if (await PopulateCooldown.HoldsUnexpiredContentAsync(_dbContext, documentId, now, cancellationToken)
                || (interval is { } declared && await PopulateCooldown.AttemptWithinIntervalAsync(
                        _dbContext, machineId, documentId, declared, now, cancellationToken)))
            {
                Response.Headers[PopulateOutcomeHeader] = "current";
                return NoContent();
            }

            // The durable-content clock (ABI 0.28, #1307), stamped BEFORE the run for the same reason the
            // runner stamps it there: the outbound request is the thing being rate-limited.
            if (interval is not null && _currentTenantAccessor.TenantId is { } stampTenant)
            {
                await PopulateCooldown.RecordAttemptAsync(
                    _dbContext, stampTenant, machineId, documentId, now, cancellationToken);
            }

            Response.Headers[PopulateOutcomeHeader] = "ran";
        }

        // The populate hook's outcome is also a HEALTH fact when it is an auto-refresh (ADR 0811), and this
        // path counts alongside the protocol one: "failing since 14:05" has to mean every attempt, or it is
        // not a duration. A failure here still surfaces to THIS caller as a 500, which is why the recording is
        // the addition rather than the reporting — the caller learns, the tenant's administrators did not.
        var healthTenant = autoRefresh && machine.ModuleId is not null ? _currentTenantAccessor.TenantId : null;

        SimplArchive.ModuleAbi.StatusResult verdict;
        try
        {
            verdict = await _engine.ExecuteTransitionAsync(machineId, transitionName, documentId, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (Exception ex) when (healthTenant is not null && ex is not OperationCanceledException)
        {
            var failedName = await _dbContext.Documents
                .Where(d => d.Id == documentId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken);
            await _health.RecordFailureAsync(
                healthTenant.Value, machine.ModuleId!, machineId, documentId, failedName ?? string.Empty,
                ex.Message, cancellationToken);
            throw;   // the caller still gets its error; recording is an addition, never a swallow
        }

        if (verdict.Satisfied)
        {
            if (healthTenant is { } okTenant)
            {
                await _health.RecordSuccessAsync(okTenant, machine.ModuleId!, machineId, documentId, cancellationToken);
            }
        }

        if (!verdict.Satisfied)
        {
            // The refusal IS the explanation (ADR 0742): the module's sentences as detail, the
            // machine-readable diagnosis as extensions.
            throw new MachineTransitionRefusedException(machineId, transitionName, verdict.Failed, machine.ModuleId);
        }

        // A transition is an ACT, and several are legal ones — signing a flight-log entry, signing a lesson
        // record — which until now left no audit trace at all (#1092). Recorded after the engine commits, so
        // an event exists exactly when the transition did; a refusal is not recorded here because it changed
        // nothing, and the refusal already reaches the caller with its reason (ADR 0742).
        //
        // The AUTO-REFRESH hook is deliberately excluded: it fires on merely opening a folder, so recording it
        // would bury every deliberate act under a stream of events that mean "somebody looked at something".
        if (!machine.Transitions[transitionName].AutoRefreshOnOpen)
        {
            var subjectName = await _dbContext.Documents
                .Where(d => d.Id == documentId)
                .Select(d => d.Name)
                .FirstOrDefaultAsync(cancellationToken);

            await _audit.RecordAsync(AuditActions.ModuleTransitionRan, "Document", documentId, subjectName,
                $"{machineId}/{transitionName}"
                    + (machine.ModuleId is { } id ? $" (module {id})" : string.Empty),
                cancellationToken: cancellationToken);
        }

        return NoContent();
    }
}
