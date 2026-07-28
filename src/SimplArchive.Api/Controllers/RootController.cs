using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The API root discovery document — see ADR "API discoverability / root endpoint design", ADR
/// "Repositories controller and Document creation". Public, no authentication required; individual
/// linked resources still enforce their own auth/ACL once followed. "admin" links to a route that
/// doesn't exist yet (separate future work) — the link exists so a client can discover it once it does.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api")]
[AllowAnonymous]
public class RootController : ControllerBase
{
    public class RootResource : HypermediaResource
    {
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new RootResource
        {
            Links =
            [
                new Link("self", "/api", "GET"),
                new Link("repositories", "/api/repositories", "GET"),
                new Link("search", "/api/search", "GET"),
                new Link("tasks", "/api/tasks", "GET"),
                new Link("reminders", "/api/reminders", "GET"),
                new Link("subscriptions", "/api/subscriptions", "GET"),
                new Link("notifications", "/api/notifications", "GET"),
                new Link("legalHolds", "/api/legal-holds", "GET"),
                new Link("recycleBin", "/api/recycle-bin", "GET"),
                new Link("checkouts", "/api/checkouts", "GET"),
                new Link("tenantSettings", "/api/tenant-settings", "GET"),
                new Link("retentionSchedule", "/api/retention/schedule", "GET"),
                new Link("whoami", "/api/diagnostics/whoami", "GET"),
                new Link("admin", "/api/admin", "GET"),
                new Link("openIdConfiguration", "/.well-known/openid-configuration", "GET"),
                new Link("openApi", "/openapi/v1.json", "GET"),
            ],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public IActionResult Head()
    {
        return NoContent();
    }
}
