using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Application.Abstractions;
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

    public SearchReindexController(SearchReindexState state, ICurrentPlatformAdministratorAccessor platformAdministratorAccessor)
    {
        _state = state;
        _platformAdministratorAccessor = platformAdministratorAccessor;
    }

    public class ReindexResource
    {
        public string Status { get; set; } = "";

        // Documents indexed by the last completed rebuild, or -1 if none has finished yet.
        public int LastIndexedCount { get; set; }
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
    public IActionResult Status()
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        return Ok(new ReindexResource
        {
            Status = _state.IsRunning ? "running" : "idle",
            LastIndexedCount = _state.LastIndexedCount,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => _platformAdministratorAccessor.PlatformAdministratorId is null ? Forbid() : NoContent();
}
