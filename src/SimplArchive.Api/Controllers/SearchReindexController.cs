using System.Xml.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Search;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Admin-triggered full search-index rebuild (ADR 0139 / 0253's deferred reindex-all): backfills documents
/// that predate indexing (or were missed while OpenSearch was down) via a blue-green alias swap. Restricted
/// to a PlatformAdministrator — the index spans every tenant, so this is platform maintenance, not a
/// per-tenant action (like TenantsController/PlatformAdministratorsController). The rebuild runs in a
/// background hosted service; POST enqueues it and returns 202, GET reports status.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/search/reindex")]
[Authorize]
public class SearchReindexController : ControllerBase
{
    private readonly SearchReindexState _state;
    private readonly ICurrentPlatformAdministratorAccessor _platformAdministratorAccessor;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly OpenSearchIndexRebuilder? _rebuilder;

    // The rebuilder is null when OpenSearch is not configured (its registration is conditional) — the health
    // fields then answer "not applicable" rather than the controller failing to construct.
    public SearchReindexController(
        SearchReindexState state, ICurrentPlatformAdministratorAccessor platformAdministratorAccessor,
        SimplArchiveDbContext dbContext, OpenSearchIndexRebuilder? rebuilder = null)
    {
        _state = state;
        _platformAdministratorAccessor = platformAdministratorAccessor;
        _dbContext = dbContext;
        _rebuilder = rebuilder;
    }

    public class ReindexResource
    {
        public string Status { get; set; } = "";

        // Documents indexed by the last completed rebuild, or -1 if none has finished yet.
        public int LastIndexedCount { get; set; }

        // Whether the "documents" alias is MISSING (#661): while true, every per-document write is gated off
        // and every search answers empty — the degradation ADR 0626 forbids leaving invisible, now stated
        // where an administrator can ask for it instead of only in one startup warning. Null when OpenSearch
        // is not configured (the Postgres fallback needs no alias) or unreachable at the moment of asking.
        [XmlElement(IsNullable = true)]
        public bool? AliasMissing { get; set; }

        // Outbox rows waiting to be indexed (#661). A number that keeps growing names its own problem —
        // including the deliberate hold while a rebuild runs, which is what makes the pause observable.
        public int PendingOutbox { get; set; }
    }

    [HttpPost]
    public IActionResult Trigger()
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        _state.Request();
        return Accepted(new ReindexResource
        {
            Status = _state.IsRunning ? "running" : "queued",
            LastIndexedCount = _state.LastIndexedCount,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        return Ok(new ReindexResource
        {
            Status = _state.IsRunning ? "running" : "idle",
            LastIndexedCount = _state.LastIndexedCount,
            AliasMissing = await AliasMissingAsync(cancellationToken),
            PendingOutbox = await _dbContext.SearchIndexOutbox.CountAsync(cancellationToken), // not tenant-scoped by design
        });
    }

    private async Task<bool?> AliasMissingAsync(CancellationToken cancellationToken)
    {
        if (_rebuilder is null)
        {
            return null; // OpenSearch not configured — the Postgres fallback has no alias to miss
        }

        try
        {
            return !await _rebuilder.AliasExistsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null; // unreachable right now — unknown, not "missing" (which would read as data loss)
        }
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => _platformAdministratorAccessor.PlatformAdministratorId is null ? Forbid() : NoContent();
}
