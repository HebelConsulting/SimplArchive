using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Modules;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Answers a machine's proposal query for its subject (ABI 0.11, ADRs 0736/0769) — "who/what could fill
/// this field?", the epic's signature feature. The handler runs under the MODULE principal (the engine's
/// act-as scope), because a proposal must read what the asking caller cannot; what comes back is the
/// module's already-filtered items, never the documents they were derived from.
/// </summary>
/// <remarks>
/// The core's gates mirror the transitions controller: the machine and proposal must exist, the declaring
/// module must be ACTIVE for the tenant (404 <c>MODULE_NOT_ACTIVE</c> otherwise), the subject must wear
/// the machine's mask — and the caller needs <c>CanEditIndexData</c>, because a proposal exists to fill an
/// index field, and a caller who cannot write the field has no use the affordance could serve (ADR 0543).
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/machine/{machineId}/proposals")]
[Authorize]
public class MachineProposalsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly StateMachineCatalog _catalog;
    private readonly StateMachineEngine _engine;

    public MachineProposalsController(
        SimplArchiveDbContext dbContext, DocumentAccessService access, StateMachineCatalog catalog, StateMachineEngine engine)
    {
        _dbContext = dbContext;
        _access = access;
        _catalog = catalog;
        _engine = engine;
    }

    /// <summary>The proposal's answer: which field it fills, and the proposable items.</summary>
    public class ProposalResource
    {
        /// <summary>The affordance's label (the declared one) — carried here so the picker labels itself
        /// from the response it just fetched, not from link metadata it would have to keep.</summary>
        public string Label { get; set; } = string.Empty;

        public string FillsField { get; set; } = string.Empty;

        public List<ProposalItemResource> Items { get; set; } = [];

        public List<Link> Links { get; set; } = [];
    }

    public class ProposalItemResource
    {
        /// <summary>What the picker writes into the field.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>Who or what this is.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Why it qualifies — already filtered to what the answer needs (ADR 0736).</summary>
        public string? Detail { get; set; }
    }

    [HttpGet("{proposalName}")]
    public async Task<IActionResult> Get(Guid documentId, string machineId, string proposalName, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(documentId, machineId, proposalName, cancellationToken);
        if (prepared is not null)
        {
            return prepared;
        }

        var (definition, items) = await _engine.ExecuteProposalAsync(machineId, proposalName, documentId, cancellationToken);
        return Ok(new ProposalResource
        {
            Label = definition.Label,
            FillsField = definition.FillsField,
            Items = [.. items.Select(i => new ProposalItemResource { Value = i.Value, Label = i.Label, Detail = i.Detail })],
            Links = [new Link("self", $"/api/documents/{documentId}/machine/{machineId}/proposals/{proposalName}", "GET")],
        });
    }

    [HttpHead("{proposalName}")]
    public async Task<IActionResult> Head(Guid documentId, string machineId, string proposalName, CancellationToken cancellationToken)
        => await PrepareAsync(documentId, machineId, proposalName, cancellationToken) ?? Ok();

    /// <summary>The shared gates; null when the request may proceed.</summary>
    private async Task<IActionResult?> PrepareAsync(Guid documentId, string machineId, string proposalName, CancellationToken cancellationToken)
    {
        if (!_catalog.Machines.TryGetValue(machineId, out var machine)
            || !machine.Proposals.ContainsKey(proposalName))
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

        var subjectMaskId = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => (Guid?)v.MaskId)
            .FirstOrDefaultAsync(cancellationToken);
        if (subjectMaskId != machine.SubjectMaskId)
        {
            return NotFound();
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanEditIndexData)
        {
            return Forbid();
        }

        return null;
    }
}
