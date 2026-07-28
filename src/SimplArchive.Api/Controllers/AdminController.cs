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
/// a tenant admin can browse into all personal spaces (their IsTenantAdmin ACL bypass already grants access; this
/// gives it a navigable home). Gated on the caller's effective IsTenantAdmin (own ∪ groups); a ServiceAccount is
/// never a tenant admin. Listing is recorded to the audit log — admin access to private spaces isn't silent.
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
        // The user's personal repository — browse it via GET /api/documents/{id}/children (the admin's ACL bypass).
        public Guid RepositoryId { get; set; }
        public bool HasChildren { get; set; }
        public bool HasSubfolders { get; set; }
    }

    public class PersonalRepositoriesResource : HypermediaResource
    {
        public List<PersonalRepositoryItem> Repositories { get; set; } = [];
    }

    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;

    [HttpGet("personal-repositories")]
    public async Task<IActionResult> ListPersonalRepositories(CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
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
                HasChildren = _dbContext.Documents.Any(c => c.ParentId == d.Id),
                HasSubfolders = _dbContext.Documents.Any(c => c.ParentId == d.Id && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id)),
            })
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Email)
            .ToListAsync(cancellationToken);

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
        await IsTenantAdminAsync(cancellationToken) ? NoContent() : Forbid();
}
