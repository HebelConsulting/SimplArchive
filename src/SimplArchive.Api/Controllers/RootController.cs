using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The API root discovery document — see ADR "API discoverability / root endpoint design", ADR
/// "Repositories controller and Document creation". Public, no authentication required; individual
/// linked resources still enforce their own auth/ACL once followed.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous, and the tenant-scoped rels are emitted to everyone: what a caller may DO is answered by the
/// resource they follow to, not by the root's list. The exception is the PLATFORM-ADMINISTRATOR rels below,
/// which are conditional — see <see cref="PlatformAdministratorLinks"/> for why that difference is not an
/// inconsistency.
/// </para>
/// <para>
/// The <c>admin</c> rel is the TENANT-admin surface (<see cref="AdminController"/>, the synthetic
/// "Administration → Users" branch), not the platform one. Said here because the name invites the other
/// reading, and because this comment claimed for a long time that the route "doesn't exist yet" — it had
/// existed for months, which is the kind of stale note that makes a reader distrust the rest.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api")]
[AllowAnonymous]
public class RootController : ControllerBase
{
    /// <summary>
    /// The four collections only a platform administrator may use, which until #1409 were reachable by NO rel —
    /// so a conforming client had to conclude they did not exist, and the one tool that needed them composed a
    /// path instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Conditional, unlike every other rel here</b>, and the difference is not arbitrary. A tenant-scoped rel
    /// is emitted to everyone because the caller is a member of the tenant either way and the followed resource
    /// answers what they may do. A platform administrator is a DIFFERENT PRINCIPAL KIND with no tenant at all
    /// (ADR 0206) — it cannot sign in to either client — so for every other caller these four are not "refused",
    /// they are not part of the API at all. Emitting them anyway would hand a signed-in user four addresses that
    /// can only ever answer 403, which is exactly the lying affordance ADR 0543 exists to prevent.
    /// </para>
    /// <para>
    /// Kept as a list rather than inlined in the array above so the conditional emission is one statement and the
    /// set is readable as a set — this is the platform surface, and the next platform endpoint belongs here rather
    /// than wherever it was first needed.
    /// </para>
    /// </remarks>
    private static readonly Link[] PlatformAdministratorLinks =
    [
        // Provisioning a tenant: the reason #1409 was filed, and `saconsole`'s one composed URL until now.
        new Link("tenants", "/api/tenants", "GET"),
        new Link("platformAdministrators", "/api/platform-administrators", "GET"),
        // KEK rotation (ADR 0821) — deliberately no client UI, because a platform administrator cannot sign in
        // to one; a rel is how the surface is reachable by the tooling that CAN act as one.
        new Link("encryptionKeys", "/api/encryption/keys", "GET"),
        new Link("searchReindex", "/api/search/reindex", "GET"),
    ];

    public class RootResource : HypermediaResource
    {
        // The server's own build version (ADR 0512), so the desktop client's self-update check can tell whether it
        // is behind THIS deployment before looking for a matching client release on GitHub. Read-only, informational.
        public string ServerVersion { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromServices] IReadOnlyList<ModuleLoader.LoadedModule> modules,
        [FromServices] ICurrentTenantAccessor tenantAccessor,
        [FromServices] ICurrentPlatformAdministratorAccessor platformAdministratorAccessor,
        [FromServices] SimplArchiveDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var resource = new RootResource
        {
            ServerVersion = ServerBuildInfo.Version,
            Links =
            [
                new Link("self", "/api", "GET"),
                // This installation's colours (ADR 0578). On the ROOT because the web client applies them
                // before anyone has signed in — a brand that appears only after login appears too late — and
                // the root is the one resource reachable anonymously.
                new Link("theme", "/api/theme", "GET"),
                new Link("repositories", "/api/repositories", "GET"),
                new Link("search", "/api/search", "GET"),
                // Acting on a SET of documents, and finding the ones that already exist by content hash —
                // neither belongs to a resource a client would otherwise be holding (issue #416).
                // TEMPLATED (RFC 6570), the root's only such link: a deep link (#761) is a human-carried URL
                // whose only payload is a document id, so its lander legitimately holds an id and nothing
                // else — the situation ADR 0543 otherwise ends by handing out full hrefs. Expanding a template
                // the server itself advertised is FOLLOWING, not composing: the server still owns the path
                // shape, and renaming the route only requires updating this one line.
                new Link("document", "/api/documents/{id}", "GET"),
                new Link("documentsBulk", "/api/documents/bulk", "GET"),
                new Link("duplicates", "/api/duplicates", "GET"),
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

                // The tenant's mail domains (#667). Advertised unconditionally, like tenantSettings beside it:
                // the LIST is readable by anyone in the tenant — which domains your own organisation receives
                // on is not privileged — and what may be CHANGED is said by the collection's own `add` rel and
                // its canManage flag, where the right is actually known (ADR 0543).
                new Link("mailDomains", "/api/tenant/mail-domains", "GET"),
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
                new Link("intray", "/api/intray", "GET"),
                // The send-to pickers' choices (issue #416). Collection roots in their own right, not actions on
                // the intray: the send dialog opens from a single item and never lists the intray, so reaching
                // these through `intray` would mean an S3 listing of every staged file to pick up two hrefs.
                new Link("intrayGroups", "/api/intray/groups", "GET"),
                new Link("intrayUsers", "/api/intray/users", "GET"),
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
        };

        // The platform-administrator surface, emitted ONLY to a platform administrator (#1409).
        if (platformAdministratorAccessor.PlatformAdministratorId is not null)
        {
            resource.Links.AddRange(PlatformAdministratorLinks);
        }

        // Module entry rels (ADR 0737): a loaded module's RootLinks appear only for a tenant whose
        // activation is ACTIVE — for everyone else (other tenants, anonymous callers, platform admins)
        // the module's surface does not exist, which is exactly what the gate on its routes answers too.
        if (modules.Count > 0 && tenantAccessor.TenantId is not null)
        {
            var withRootLinks = modules.Where(m => m.Module.RootLinks.Count > 0).ToList();
            if (withRootLinks.Count > 0)
            {
                var ids = withRootLinks.Select(m => m.Module.ModuleId).ToList();
                var activations = await dbContext.ModuleActivations
                    .Where(a => ids.Contains(a.ModuleId))
                    .ToListAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                foreach (var loaded in withRootLinks)
                {
                    var activation = activations.FirstOrDefault(a => a.ModuleId == loaded.Module.ModuleId);
                    if (activation is not null && ModuleActivationPolicy.IsActive(activation, now))
                    {
                        resource.Links.AddRange(loaded.Module.RootLinks
                            .Select(l => new Link(l.Rel, l.Path, l.Method)));
                    }
                }
            }
        }

        return Ok(resource);
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public IActionResult Head()
    {
        return NoContent();
    }
}
