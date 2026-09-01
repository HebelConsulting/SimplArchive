using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Search;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Search;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// A user's saved searches (ADR "Saved searches"; sharing "Scoped saved-search sharing") — a named, reusable
/// snapshot of a full search (the assembled query-params string). User-only: a ServiceAccount /
/// PlatformAdministrator has no saved searches. The list is the caller's own searches plus every search shared
/// with them — Everyone-scoped, or Specific-scoped to them / a group they're in (membership flows down); only the
/// owner may edit/share/delete one. A small bounded catalog (like /api/masks), so not paginated.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/saved-searches")]
[Authorize]
public class SavedSearchesController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public SavedSearchesController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor, ICurrentTenantAccessor currentTenantAccessor)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
    }

    public class SavedSearchResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string QueryString { get; set; } = string.Empty;
        // The visibility scope (ADR "Scoped saved-search sharing"). IsMine gates the client's edit/share/delete; a
        // search shared by someone else shows OwnerName and is run-only (or "Save a copy" as a new private one).
        public int ShareScope { get; set; }
        public string ShareScopeName { get; set; } = string.Empty;
        public bool IsMine { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class SavedSearchesResource : HypermediaResource
    {
        public List<SavedSearchResource> SavedSearches { get; set; } = [];
    }

    // A principal reference in a create/update request (Type = "user" | "group").
    public class SharePrincipal
    {
        public string Type { get; set; } = string.Empty;
        public Guid Id { get; set; }
    }

    public class CreateSavedSearchRequest
    {
        public string Name { get; set; } = string.Empty;
        public string QueryString { get; set; } = string.Empty;
        public int ShareScope { get; set; }
        public List<SharePrincipal>? Shares { get; set; }
    }

    public class UpdateSavedSearchRequest
    {
        public string Name { get; set; } = string.Empty;
        public string QueryString { get; set; } = string.Empty;
        public int ShareScope { get; set; }
        public List<SharePrincipal>? Shares { get; set; }
    }

    // The current specific-principal grants on a search (owner-only read), for the share dialog.
    public class SharesResource : HypermediaResource
    {
        public List<ShareGrantResource> Shares { get; set; } = [];
    }

    public class ShareGrantResource
    {
        public string PrincipalType { get; set; } = string.Empty;
        public Guid PrincipalId { get; set; }
        public string PrincipalName { get; set; } = string.Empty;
    }

    // The picker options for the share dialog — active users + groups (any authenticated user, a bounded list).
    public class ShareTargetsResource : HypermediaResource
    {
        public List<ShareTargetUser> Users { get; set; } = [];
        public List<ShareTargetGroup> Groups { get; set; } = [];
    }

    public class ShareTargetUser
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    public class ShareTargetGroup
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        // The caller's effective group set (direct memberships + descendants, membership flows down) — for
        // resolving group-targeted shares.
        var groupIds = await SimplArchive.Infrastructure.Acl.GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, userId, cancellationToken);

        // Visible = the caller's own searches, plus Everyone-scoped searches, plus Specific-scoped searches
        // shared with the caller directly or with a group in their effective set.
        var items = await _dbContext.SavedSearches
            .Where(s => s.UserId == userId
                || s.ShareScope == ShareScope.Everyone
                || _dbContext.SavedSearchShares.Any(sh => sh.SavedSearchId == s.Id
                    && (sh.UserId == userId || (sh.GroupId != null && groupIds.Contains(sh.GroupId.Value)))))
            .OrderBy(s => s.Name)
            .Select(s => new SavedSearchResource
            {
                Id = s.Id,
                Name = s.Name,
                QueryString = s.QueryString,
                ShareScope = (int)s.ShareScope,
                ShareScopeName = s.ShareScope.ToString(),
                IsMine = s.UserId == userId,
                OwnerName = _dbContext.Users.Where(u => u.Id == s.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? "",
                CreatedAt = s.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        // A saved search addresses itself and its shares; only the OWNER may rewrite or delete one, so those two
        // rels are absent on a search shared with you — the client greys the row's actions from the rels rather
        // than re-deriving ownership from IsMine (issue #416).
        foreach (var item in items)
        {
            item.Links = item.IsMine
                ?
                [
                    new Link("self", $"/api/saved-searches/{item.Id}", "PUT"),
                    new Link("delete", $"/api/saved-searches/{item.Id}", "DELETE"),
                    new Link("shares", $"/api/saved-searches/{item.Id}/shares", "GET"),
                ]
                : [];
        }

        return Ok(new SavedSearchesResource
        {
            SavedSearches = items,
            Links =
            [
                new Link("self", "/api/saved-searches", "GET"),
                // Who a search may be shared WITH — a collection in its own right, needed by the share dialog
                // before it has picked anything.
                new Link("share-targets", "/api/saved-searches/share-targets", "GET"),
            ],
        });
    }

    [HttpHead]
    public IActionResult Head() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSavedSearchRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var name = request.Name?.Trim() ?? "";
        var query = request.QueryString?.Trim() ?? "";
        if (name.Length == 0 || query.Length == 0)
        {
            throw new InvalidSavedSearchException();
        }

        var scope = ParseScope(request.ShareScope);
        var saved = new SavedSearch
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Name = name,
            QueryString = query,
            ShareScope = scope,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.SavedSearches.Add(saved);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new SavedSearchNameConflictException(); // the unique (TenantId, UserId, Name) index
        }

        await ApplySharesAsync(saved.Id, tenantId, scope, request.Shares, cancellationToken);

        return CreatedAtAction(nameof(List), new SavedSearchResource
        {
            Id = saved.Id,
            Name = saved.Name,
            QueryString = saved.QueryString,
            ShareScope = (int)saved.ShareScope,
            ShareScopeName = saved.ShareScope.ToString(),
            IsMine = true,
            CreatedAt = saved.CreatedAt,
        });
    }

    private static ShareScope ParseScope(int value) =>
        Enum.IsDefined(typeof(ShareScope), value) ? (ShareScope)value : throw new InvalidSavedSearchException();

    // Replaces a search's specific-principal grants. Clears any existing rows, then (only when Specific) inserts a
    // row per requested principal that resolves to a real active user / group in the tenant (unknown ones are
    // dropped). A non-Specific scope clears the grants entirely.
    private async Task ApplySharesAsync(Guid savedSearchId, Guid tenantId, ShareScope scope, List<SharePrincipal>? shares, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.SavedSearchShares.Where(s => s.SavedSearchId == savedSearchId).ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _dbContext.SavedSearchShares.RemoveRange(existing);
        }

        if (scope == ShareScope.Specific && shares is { Count: > 0 })
        {
            var userIds = shares.Where(s => string.Equals(s.Type, "user", StringComparison.OrdinalIgnoreCase)).Select(s => s.Id).Distinct().ToList();
            var groupIds = shares.Where(s => string.Equals(s.Type, "group", StringComparison.OrdinalIgnoreCase)).Select(s => s.Id).Distinct().ToList();
            var validUserIds = await _dbContext.Users.Where(u => userIds.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(cancellationToken);
            var validGroupIds = await _dbContext.Groups.Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(cancellationToken);

            foreach (var uid in validUserIds)
            {
                _dbContext.SavedSearchShares.Add(new SavedSearchShare { Id = Guid.NewGuid(), TenantId = tenantId, SavedSearchId = savedSearchId, UserId = uid, CreatedAt = DateTimeOffset.UtcNow });
            }

            foreach (var gid in validGroupIds)
            {
                _dbContext.SavedSearchShares.Add(new SavedSearchShare { Id = Guid.NewGuid(), TenantId = tenantId, SavedSearchId = savedSearchId, GroupId = gid, CreatedAt = DateTimeOffset.UtcNow });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // Owner-only edit (rename / re-query / share-unshare). A non-owner (incl. of a shared search they can see)
    // gets 404 — only the creator may change it (ADR "Shareable saved searches").
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSavedSearchRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var saved = await _dbContext.SavedSearches.SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);
        if (saved is null)
        {
            return NotFound();
        }

        var name = request.Name?.Trim() ?? "";
        var query = request.QueryString?.Trim() ?? "";
        if (name.Length == 0 || query.Length == 0)
        {
            throw new InvalidSavedSearchException();
        }

        var scope = ParseScope(request.ShareScope);
        saved.Name = name;
        saved.QueryString = query;
        saved.ShareScope = scope;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new SavedSearchNameConflictException();
        }

        await ApplySharesAsync(saved.Id, saved.TenantId, scope, request.Shares, cancellationToken);

        return Ok(new SavedSearchResource
        {
            Id = saved.Id,
            Name = saved.Name,
            QueryString = saved.QueryString,
            ShareScope = (int)saved.ShareScope,
            ShareScopeName = saved.ShareScope.ToString(),
            IsMine = true,
            CreatedAt = saved.CreatedAt,
        });
    }

    // The current specific-principal grants on a search — owner-only (a non-owner gets 404). Backs the share
    // dialog when editing an existing search.
    [HttpGet("{id:guid}/shares")]
    public async Task<IActionResult> GetShares(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        if (!await _dbContext.SavedSearches.AnyAsync(s => s.Id == id && s.UserId == userId, cancellationToken))
        {
            return NotFound();
        }

        var shares = await _dbContext.SavedSearchShares
            .Where(s => s.SavedSearchId == id)
            .Select(s => new ShareGrantResource
            {
                PrincipalType = s.UserId != null ? "user" : "group",
                PrincipalId = s.UserId ?? s.GroupId!.Value,
                PrincipalName = s.UserId != null
                    ? _dbContext.Users.Where(u => u.Id == s.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? ""
                    : _dbContext.Groups.Where(g => g.Id == s.GroupId).Select(g => g.Name).FirstOrDefault() ?? "",
            })
            .ToListAsync(cancellationToken);

        return Ok(new SharesResource { Shares = shares, Links = [new Link("self", $"/api/saved-searches/{id}/shares", "GET")] });
    }

    [HttpHead("{id:guid}/shares")]
    public async Task<IActionResult> GetSharesHead(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        return await _dbContext.SavedSearches.AnyAsync(s => s.Id == id && s.UserId == userId, cancellationToken) ? NoContent() : NotFound();
    }

    // The picker options for the share dialog — active users + groups. Any authenticated user (a bounded
    // directory, like the assignable-reviewers list); not paginated.
    [HttpGet("share-targets")]
    public async Task<IActionResult> ShareTargets(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is null)
        {
            return Forbid();
        }

        var users = await _dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new ShareTargetUser { Id = u.Id, DisplayName = u.DisplayName })
            .ToListAsync(cancellationToken);
        var groups = await _dbContext.Groups
            .OrderBy(g => g.Name)
            .Select(g => new ShareTargetGroup { Id = g.Id, Name = g.Name })
            .ToListAsync(cancellationToken);

        return Ok(new ShareTargetsResource { Users = users, Groups = groups, Links = [new Link("self", "/api/saved-searches/share-targets", "GET")] });
    }

    [HttpHead("share-targets")]
    public IActionResult ShareTargetsHead() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var saved = await _dbContext.SavedSearches.SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);
        if (saved is null)
        {
            return NotFound();
        }

        _dbContext.SavedSearches.Remove(saved);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
