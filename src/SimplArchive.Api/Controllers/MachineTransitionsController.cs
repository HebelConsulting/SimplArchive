using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Modules;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

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
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly StateMachineCatalog _catalog;
    private readonly StateMachineEngine _engine;

    public MachineTransitionsController(
        SimplArchiveDbContext dbContext, DocumentAccessService access, StateMachineCatalog catalog, StateMachineEngine engine)
    {
        _dbContext = dbContext;
        _access = access;
        _catalog = catalog;
        _engine = engine;
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

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        var verdict = await _engine.ExecuteTransitionAsync(machineId, transitionName, documentId, DateTimeOffset.UtcNow, cancellationToken);
        if (!verdict.Satisfied)
        {
            // The refusal IS the explanation (ADR 0742): the module's sentences as detail, the
            // machine-readable diagnosis as extensions.
            throw new MachineTransitionRefusedException(machineId, transitionName, verdict.Failed);
        }

        return NoContent();
    }
}
