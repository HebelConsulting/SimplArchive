using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Tenant-admin administration surface (ADR "Tenant-admin Administration → Users view"). Backs a synthetic
/// "Administration → Users" branch in the clients' repository tree: it lists every user's personal repository so
/// an administrator can browse into all personal spaces; it gives that access a navigable home. Gated on the
/// caller's effective CanAccessWithoutGrant (own ∪ groups) since ADR 0670 — NOT on IsTenantAdmin, because the
/// bypass no longer reaches inside a personal space and the right is what does. An admin who revokes their own
/// x-ray therefore stops seeing this, which is the honest answer rather than a listing they cannot open.
/// Listing is recorded to the audit log — access to private spaces isn't silent.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IAuditRecorder _auditRecorder;

    public AdminController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IAuditRecorder auditRecorder)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _auditRecorder = auditRecorder;
    }

    public class PersonalRepositoryItem
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public bool UserIsActive { get; set; }
        // The user's personal repository, with its advertised addresses (#443): `document` (the repository seen
        // as a document) and `children` (browse it, via the admin's ACL bypass) — a row naming a repository
        // without its address leaves the client an id it can only compose from.
        public Guid RepositoryId { get; set; }
        public bool HasChildren { get; set; }
        public bool HasSubfolders { get; set; }
        public List<Link>? Links { get; set; }
    }

    public class PersonalRepositoriesResource : HypermediaResource
    {
        public List<PersonalRepositoryItem> Repositories { get; set; } = [];
    }

    private async Task<bool> CanAccessWithoutGrantAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanAccessWithoutGrant;

    // The administration index. The API root has always advertised an `admin` rel pointing here, and until now
    // nothing answered it — a dangling rel, which under ADR 0543 is worse than no rel at all: a client is
    // supposed to be able to follow what it is offered, so the only way to reach personal-repositories was to
    // compose its path (issue #416). Listing the sub-resources here is what makes that unnecessary.
    //
    // The index itself is open — it says what exists — but the personal-repositories REL is gated (ADR 0670):
    // a missing rel means "not available to you, here, now" (ADR 0543), and for an administrator who revoked
    // their own CanAccessWithoutGrant that is exactly true. Advertising it anyway would offer a door that
    // answers 403, which is the affordance this project treats as worse than none.
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var links = new List<Link> { new("self", "/api/admin", "GET") };
        if (await CanAccessWithoutGrantAsync(cancellationToken))
        {
            links.Add(new Link("personal-repositories", "/api/admin/personal-repositories", "GET"));
        }

        return Ok(new AdminIndexResource { Links = links });
    }

    [HttpHead]
    public IActionResult IndexHead() => NoContent();

    public class AdminIndexResource : HypermediaResource;

    [HttpGet("personal-repositories")]
    public async Task<IActionResult> ListPersonalRepositories(CancellationToken cancellationToken)
    {
        if (!await CanAccessWithoutGrantAsync(cancellationToken))
        {
            return Forbid();
        }

        // Every personal repository in the tenant (a root Document flagged PersonalOfUserId) + its owner. The
        // tenant + soft-delete query filters scope it to the caller's tenant automatically.
        var items = await _dbContext.Documents
            .Where(d => d.PersonalOfUserId != null)
            .Join(_dbContext.Users, d => d.PersonalOfUserId, u => u.Id, (d, u) => new PersonalRepositoryItem
            {
                UserId = u.Id,
                DisplayName = u.DisplayName,
                Email = u.Email,
                UserIsActive = u.IsActive,
                RepositoryId = d.Id,
                // Child document/subfolder OR a reference filed into it (issue #376).
                HasChildren = _dbContext.Documents.Any(c => c.ParentId == d.Id)
                    || _dbContext.DocumentReferences.Any(x => x.ParentFolderId == d.Id),
                HasSubfolders = _dbContext.Documents.Any(c => c.ParentId == d.Id && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id)),
            })
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Email)
            .ToListAsync(cancellationToken);

        foreach (var item in items)
        {
            item.Links =
            [
                new Link("document", $"/api/documents/{item.RepositoryId}", "GET"),
                new Link("children", $"/api/documents/{item.RepositoryId}/children", "GET"),
            ];
        }

        // Record the admin's access to personal spaces (not silent) — after the read succeeds.
        await _auditRecorder.RecordAsync(
            AuditActions.AdminViewedPersonalSpaces,
            details: $"Listed {items.Count} personal {(items.Count == 1 ? "space" : "spaces")}.",
            cancellationToken: cancellationToken);

        return Ok(new PersonalRepositoriesResource
        {
            Repositories = items,
            Links = [new Link("self", "/api/admin/personal-repositories", "GET")],
        });
    }

    [HttpHead("personal-repositories")]
    public async Task<IActionResult> ListPersonalRepositoriesHead(CancellationToken cancellationToken) =>
        await CanAccessWithoutGrantAsync(cancellationToken) ? NoContent() : Forbid();
}
