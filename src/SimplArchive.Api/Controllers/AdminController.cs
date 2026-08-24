using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Notifications;
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
    private readonly INotificationService _notifications;

    public AdminController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IAuditRecorder auditRecorder,
        INotificationService notifications)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _auditRecorder = auditRecorder;
        _notifications = notifications;
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

    // The lifecycle right, not the browsing one (ADR 0672): taking over a departed person's space is the same
    // act as deactivating them, and it is audited and announced rather than silent.
    private async Task<bool> CanManageUsersAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageUsers;

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

        var canTakeOver = await CanManageUsersAsync(cancellationToken);

        foreach (var item in items)
        {
            item.Links =
            [
                new Link("document", $"/api/documents/{item.RepositoryId}", "GET"),
                new Link("children", $"/api/documents/{item.RepositoryId}/children", "GET"),

                // Opening a personal space lists its children AND the shortcuts filed in it, exactly as opening
                // any other folder does — the same pair, for the same reason, the repositories listing carries
                // (#735). Without it the desktop tree CRASHED on expanding a user here: its loader follows both
                // rels, and the one that was never advertised threw on a path with no handler above it.
                new Link("references", $"/api/documents/{item.RepositoryId}/references", "GET"),
            ];

            // Offered only to a caller who may actually do it (ADR 0543): a missing rel means "not available
            // to you, here, now", so a client without CanManageUsers simply has no button rather than one that
            // answers 403. Taking over your OWN space is meaningless — you already hold every right on it.
            if (canTakeOver && item.UserId != _currentUserAccessor.UserId)
            {
                item.Links.Add(new Link(
                    "take-over", $"/api/admin/personal-repositories/{item.UserId}/take-over", "POST"));
            }
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

    /// <summary>
    /// Grants the calling administrator full rights on one user's personal space (ADR 0672).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Privacy here means no SILENT access, not no access. Offboarding and GDPR both need a way into a space
    /// nobody else can reach, and the alternatives were worse: deferring it ships a lockout, and letting
    /// deactivation lift exclusivity makes access appear at the exact moment the owner can no longer see it.
    /// So the way in is explicit, audited, and announced to the owner.
    /// </para>
    /// <para>
    /// Gated on <b>CanManageUsers</b>, not on CanAccessWithoutGrant: taking over a departed person's space is a
    /// user-lifecycle act, the same right that deactivates them. An administrator who gave up their own x-ray
    /// may still do this, and that is not a hole — the act is recorded and the owner is told, which is exactly
    /// the property the privacy rule protects.
    /// </para>
    /// <para>
    /// A verb in the path, justified against #694 the way <c>/restore</c> is: this is a genuine transition, not
    /// a create, replace or delete of anything.
    /// </para>
    /// </remarks>
    [HttpPost("personal-repositories/{userId:guid}/take-over")]
    public async Task<IActionResult> TakeOverPersonalRepository(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (_currentUserAccessor.UserId is not { } actorId)
        {
            return Forbid();
        }

        var root = await _dbContext.Documents
            .SingleOrDefaultAsync(d => d.PersonalOfUserId == userId, cancellationToken);

        if (root is null)
        {
            return NotFound();
        }

        var owner = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.IsActive })
            .SingleAsync(cancellationToken);

        // Get-or-update rather than insert: AclEntry carries a partial unique index per principal per document,
        // so a second take-over of the same space would violate it. Idempotent is also the honest behaviour —
        // asking twice for access you already hold is not an error.
        var grant = await _dbContext.AclEntries
            .SingleOrDefaultAsync(a => a.DocumentId == root.Id && a.UserId == actorId, cancellationToken);

        if (grant is null)
        {
            grant = new AclEntry
            {
                Id = Guid.NewGuid(),
                TenantId = root.TenantId,
                DocumentId = root.Id,
                UserId = actorId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _dbContext.AclEntries.Add(grant);
        }

        grant.CanSee = true;
        grant.CanReadContent = true;
        grant.CanEditContent = true;
        grant.CanEditIndexData = true;
        grant.CanDelete = true;
        grant.CanCreateSubItems = true;
        grant.CanManagePermissions = true;
        grant.CanMove = true;
        grant.CanAnnotate = true;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditRecorder.RecordAsync(
            AuditActions.PersonalSpaceTakenOver,
            "Document",
            root.Id,
            root.Name,
            $"Took over the personal space of '{owner.DisplayName}'.",
            cancellationToken: cancellationToken);

        // Told, not merely recorded — and only while there is somebody to tell. A deactivated owner cannot read
        // notifications, so for them the audit log is the record, which is what offboarding needs anyway.
        if (owner.IsActive)
        {
            await _notifications.NotifyAsync(
                userId,
                NotificationType.PersonalSpaceTakenOver,
                "An administrator took over your personal space",
                $"Your personal space '{root.Name}' was taken over by an administrator, who now has full "
                + "access to it.",
                root.Id,
                cancellationToken);
        }

        return Ok(new TakeOverResource
        {
            RepositoryId = root.Id,
            Links =
            [
                new Link("document", $"/api/documents/{root.Id}", "GET"),
                new Link("children", $"/api/documents/{root.Id}/children", "GET"),
                // The grant is an ordinary ACL entry and is revoked like one, so the way to undo this is the
                // permissions dialog the caller already knows (ADR 0672).
                new Link("acl-entries", $"/api/documents/{root.Id}/acl-entries", "GET"),
            ],
        });
    }

    public class TakeOverResource : HypermediaResource
    {
        public Guid RepositoryId { get; set; }
    }

    [HttpHead("personal-repositories")]
    public async Task<IActionResult> ListPersonalRepositoriesHead(CancellationToken cancellationToken) =>
        await CanAccessWithoutGrantAsync(cancellationToken) ? NoContent() : Forbid();
}
