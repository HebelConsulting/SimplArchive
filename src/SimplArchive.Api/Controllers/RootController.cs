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
        // The server's own build version (ADR 0512), so the desktop client's self-update check can tell whether it
        // is behind THIS deployment before looking for a matching client release on GitHub. Read-only, informational.
        public string ServerVersion { get; set; } = "";
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new RootResource
        {
            ServerVersion = ServerBuildInfo.Version,
            Links =
            [
                new Link("self", "/api", "GET"),
                new Link("repositories", "/api/repositories", "GET"),
                new Link("search", "/api/search", "GET"),
                new Link("tasks", "/api/tasks", "GET"),
                new Link("reminders", "/api/reminders", "GET"),
                new Link("subscriptions", "/api/subscriptions", "GET"),
                new Link("notifications", "/api/notifications", "GET"),
                // The bell badge's count (issue #416). A deliberate, narrow exception to the "collection roots
                // only" rule below: every client needs this number BEFORE it has any reason to fetch the
                // collection, and it polls it. Reaching it through the collection's own `unread-count` rel would
                // mean fetching a page of notifications to learn how many are unread — two round trips, one of
                // them large, to render a digit. Paying that is how a codebase talks itself back into string
                // paths. It is its own addressable resource, not an action on the collection.
                new Link("notificationsUnreadCount", "/api/notifications/unread-count", "GET"),
                new Link("legalHolds", "/api/legal-holds", "GET"),
                new Link("recycleBin", "/api/recycle-bin", "GET"),
                new Link("checkouts", "/api/checkouts", "GET"),
                // Everything the caller has shared outside the system (ADR 0546) — a top-level collection, so
                // its href belongs here rather than being composed by each client.
                new Link("externalLinks", "/api/external-links", "GET"),
                new Link("tenantSettings", "/api/tenant-settings", "GET"),
                new Link("retentionSchedule", "/api/retention/schedule", "GET"),
                // The searchable-PDF backfill (issue #416): GET reports how many versions still need one, POST
                // starts the sweep. Unlike the maintenance actions on tenant-settings, this hangs off no
                // collection a client has already fetched — it is its own small resource, so the root is where
                // it can be reached at all.
                new Link("searchablePdfBackfill", "/api/searchable-pdf/backfill", "GET"),
                // The remaining top-level collections (issue #416). Only COLLECTION ROOTS belong here: an action
                // ON a collection — purging the recycle bin, exporting the audit log — is a rel on that
                // collection's own resource, which the client has already fetched. Listing every route here would
                // make the root a flat URL registry, which is the opposite of what ADR 0543 asks for.
                new Link("auditEvents", "/api/audit-events", "GET"),
                new Link("groups", "/api/groups", "GET"),
                new Link("inbox", "/api/inbox", "GET"),
                // The send-to pickers' choices (issue #416). Collection roots in their own right, not actions on
                // the inbox: the send dialog opens from a single item and never lists the inbox, so reaching
                // these through `inbox` would mean an S3 listing of every staged file to pick up two hrefs.
                new Link("inboxGroups", "/api/inbox/groups", "GET"),
                new Link("inboxUsers", "/api/inbox/users", "GET"),
                new Link("masks", "/api/masks", "GET"),
                // The searchable index-field catalogue (issue #416) — a collection in its own right, read by the
                // search UI before any search has been run, so there is no search response to hang it off.
                new Link("searchFields", "/api/search/fields", "GET"),
                new Link("ocrLanguages", "/api/ocr-languages", "GET"),
                new Link("savedSearches", "/api/saved-searches", "GET"),
                new Link("sensitivityLabels", "/api/sensitivity-labels", "GET"),
                new Link("serviceAccounts", "/api/service-accounts", "GET"),
                new Link("tags", "/api/tags", "GET"),
                new Link("users", "/api/users", "GET"),
                // The caller's own account — everything that belongs to THEM rather than the tenant hangs
                // off this rather than off the root, which is what keeps the root a set of collections
                // instead of a URL registry (issue #416).
                new Link("me", "/api/me", "GET"),
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
