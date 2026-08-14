using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Principals;
using SimplArchive.Api.Errors.Exceptions.Authorization;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Groups;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "User/Group management endpoints" — the only Api path to create/rename/delete a Group
/// and manage its GroupMembership; previously this only ever happened via direct DB seeding. Every action
/// requires the caller's own CanManageUsers (the same right UsersController uses — Groups only exist here
/// to organize Users for ACL purposes, so one right covers both) — either a ServiceAccount or a logged-in
/// User (see ADR "User support for ServiceAccount/User/Group/Mask management endpoints").
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/groups")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IClearanceResolver _clearanceResolver;
    private readonly IAuditRecorder _audit;

    public GroupsController(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IClearanceResolver clearanceResolver,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _clearanceResolver = clearanceResolver;
        _audit = audit;
    }

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class GroupResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public Guid? ParentGroupId { get; set; }

        // The tenant-wide system-level rights (ADR "Users & groups administration tab") — read here, set
        // via PUT /api/groups/{id}/rights. Stored/assignable but not yet enforced for member users (a
        // deferred follow-up).
        public SystemRights Rights { get; set; } = new();
    }

    public class GroupsListResource : HypermediaResource
    {
        public List<GroupResource> Groups { get; set; } = [];
    }

    public class CreateGroupRequest
    {
        public string Name { get; set; } = "";

        public Guid? ParentGroupId { get; set; }
    }

    public class RenameGroupRequest
    {
        public string Name { get; set; } = "";
    }

    public class MemberResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Email { get; set; } = "";

        public string DisplayName { get; set; } = "";
    }

    public class MembersListResource : HypermediaResource
    {
        public List<MemberResource> Members { get; set; } = [];
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGroupRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (request.ParentGroupId is { } parentGroupId && !await _dbContext.Groups.AnyAsync(g => g.Id == parentGroupId, cancellationToken))
        {
            return NotFound();
        }

        var group = new Group
        {
            Id = Guid.NewGuid(),
            TenantId = _currentTenantAccessor.TenantId!.Value,
            Name = request.Name,
            ParentGroupId = request.ParentGroupId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Groups.Add(group);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Group's own sibling-name-uniqueness check (SimplArchiveDbContext.SaveChanges, ADR "Group
            // name uniqueness scope") — a brand-new Group can't yet be its own ancestor, so the cycle
            // check never actually fires here.
            throw new GroupNameConflictException();
        }

        await _audit.RecordAsync(AuditActions.GroupCreated, "Group", group.Id, group.Name, cancellationToken: cancellationToken);

        var resource = BuildResource(group);

        return CreatedAtAction(nameof(Get), new { groupId = group.Id }, resource);
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.Groups.AsQueryable();

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(g => g.CreatedAt > cursorCreatedAt || (g.CreatedAt == cursorCreatedAt && g.Id > cursorId));
        }

        var fetched = await query.OrderBy(g => g.CreatedAt).ThenBy(g => g.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new GroupsListResource
        {
            Groups = page.Select(BuildResource).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public async Task<IActionResult> HeadList(CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpGet("{groupId:guid}")]
    public async Task<IActionResult> Get(Guid groupId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group is null)
        {
            return NotFound();
        }

        return Ok(BuildResource(group));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{groupId:guid}")]
    public async Task<IActionResult> Head(Guid groupId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var exists = await _dbContext.Groups.AnyAsync(g => g.Id == groupId, cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    // Renames Name only — ParentGroupId stays immutable through this endpoint; reparenting (and its own
    // cycle-detection interaction) is deferred, out of scope for this slice. See ADR "User/Group
    // management endpoints".
    [HttpPut("{groupId:guid}")]
    public async Task<IActionResult> Rename(Guid groupId, [FromBody] RenameGroupRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group is null)
        {
            return NotFound();
        }

        group.Name = request.Name;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new GroupNameConflictException();
        }

        return Ok(BuildResource(group));
    }

    // A real row delete — Group has no soft-delete/IsActive concept at all today. Rejected if the group
    // still has any child Group or GroupMembership row — no cascade, unlike Document's recycle-bin
    // cascade, since Group has no recycle bin to catch a mistake. See ADR "User/Group management
    // endpoints".
    [HttpDelete("{groupId:guid}")]
    public async Task<IActionResult> Delete(Guid groupId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group is null)
        {
            return NotFound();
        }

        var hasChildren = await _dbContext.Groups.AnyAsync(g => g.ParentGroupId == groupId, cancellationToken);
        var hasMembers = await _dbContext.GroupMemberships.AnyAsync(m => m.GroupId == groupId, cancellationToken);

        if (hasChildren || hasMembers)
        {
            throw new GroupNotEmptyException();
        }

        _dbContext.Groups.Remove(group);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.GroupDeleted, "Group", group.Id, group.Name, cancellationToken: cancellationToken);

        return NoContent();
    }

    // Idempotently ensures the membership exists — same principal-keyed-PUT shape AclEntriesController
    // already established (ADR "ACL grant management endpoints"), fitting here too since GroupMembership
    // carries no data beyond the relationship itself.
    // The member arrives in the BODY, not in the path (issue #416). Keyed on (group, user), an add has no
    // address until a user is chosen — and the user being added is by definition NOT in the members collection
    // yet, so no resource a client holds could ever advertise "/members/{thatUser}". As a POST to the collection
    // it becomes a plain rel-follow: the members collection advertises `add-member`, and the chosen principal
    // travels as data. The keyed PUT is gone rather than kept alongside; two ways to do one thing is how a
    // client ends up composing the path again (ADR 0543).
    [HttpPost("{groupId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid groupId, [FromBody] AddMemberRequest request, CancellationToken cancellationToken)
    {
        var userId = request.UserId;
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var group = await _dbContext.Groups.Where(g => g.Id == groupId).Select(g => new { g.TenantId }).SingleOrDefaultAsync(cancellationToken);

        if (group is null || !await _dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return NotFound();
        }

        var alreadyMember = await _dbContext.GroupMemberships.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, cancellationToken);

        if (!alreadyMember)
        {
            _dbContext.GroupMemberships.Add(new GroupMembership { TenantId = group.TenantId, GroupId = groupId, UserId = userId });
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync(AuditActions.GroupMemberAdded, "Group", groupId, await GroupNameAsync(groupId, cancellationToken), $"member: {await UserNameAsync(userId, cancellationToken)}", cancellationToken: cancellationToken);
        }

        return NoContent();
    }

    public class AddMemberRequest
    {
        public Guid UserId { get; set; }
    }

    private Task<string?> GroupNameAsync(Guid groupId, CancellationToken cancellationToken) =>
        _dbContext.Groups.Where(g => g.Id == groupId).Select(g => g.Name).SingleOrDefaultAsync(cancellationToken);

    private Task<string?> UserNameAsync(Guid userId, CancellationToken cancellationToken) =>
        _dbContext.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken);

    // 404 if the membership doesn't exist, 204 if removed — same not-found-vs-removed distinction
    // AclEntriesController.Revoke already uses.
    [HttpDelete("{groupId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid groupId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var membership = await _dbContext.GroupMemberships.SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return NotFound();
        }

        _dbContext.GroupMemberships.Remove(membership);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.GroupMemberRemoved, "Group", groupId, await GroupNameAsync(groupId, cancellationToken), $"member: {await UserNameAsync(userId, cancellationToken)}", cancellationToken: cancellationToken);

        return NoContent();
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints".
    // GroupMembership itself has no CreatedAt, so this sorts by the joined User's CreatedAt/Id instead —
    // what's actually being listed is User summaries, and this keeps the cursor shape uniform with every
    // other list endpoint rather than special-casing a composite-key cursor.
    [HttpGet("{groupId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid groupId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!await _dbContext.Groups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return NotFound();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.GroupMemberships
            .Where(m => m.GroupId == groupId)
            .Join(_dbContext.Users, m => m.UserId, u => u.Id, (m, u) => u);

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(u => u.CreatedAt > cursorCreatedAt || (u.CreatedAt == cursorCreatedAt && u.Id > cursorId));
        }

        var fetched = await query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(ListMembers), new { groupId, cursor, limit = pageSize })!, "GET"),
            // Adding someone to this group — the chosen user travels in the body, so one address serves them all.
            new("add-member", $"/api/groups/{groupId}/members", "POST"),
        };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(ListMembers), new { groupId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new MembersListResource
        {
            Members = page.Select(u => new MemberResource
            {
                Id = u.Id,
                Email = u.Email,
                DisplayName = u.DisplayName,
                // Removing THIS membership is addressed by the pair, so the rel belongs on the member row — the
                // only resource that knows both ends of it. (Adding one has no such home: the user being added
                // is not in this collection yet, so no resource the client holds can advertise that address.
                // A rel becomes possible only if the API takes the member in the BODY of a POST here.)
                Links = new List<Link>
                {
                    new("self", $"/api/users/{u.Id}", "GET"),
                    new("remove", $"/api/groups/{groupId}/members/{u.Id}", "DELETE"),
                },
            }).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{groupId:guid}/members")]
    public async Task<IActionResult> HeadListMembers(Guid groupId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!await _dbContext.Groups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return NotFound();
        }

        return NoContent();
    }

    // Sets the group's full system-rights bundle — see ADR "Users & groups administration tab". Same gate +
    // escalation cap as UsersController.SetRights (caller's CanManageUsers, then SystemRightsPolicy). Group
    // rights are stored/assignable but not yet enforced for member users (a deferred follow-up).
    [HttpPut("{groupId:guid}/rights")]
    public async Task<IActionResult> SetRights(Guid groupId, [FromBody] SystemRights request, CancellationToken cancellationToken)
    {
        var caller = await GetCallerSystemRightsAsync(cancellationToken);

        if (!caller.CanManageUsers)
        {
            return Forbid();
        }

        var group = await _dbContext.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group is null)
        {
            return NotFound();
        }

        if (!SystemRightsPolicy.CanApply(caller, ReadRights(group), request))
        {
            throw InsufficientRightsToGrantException.OnSystemRights();
        }

        ApplyRights(group, request);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.GroupRightsChanged, "Group", group.Id, group.Name, Users.SystemRightsMapping.Describe(request), cancellationToken: cancellationToken);

        return Ok(BuildResource(group));
    }

    private static GroupResource BuildResource(Group group)
    {
        return new GroupResource
        {
            Id = group.Id,
            Name = group.Name,
            ParentGroupId = group.ParentGroupId,
            Rights = ReadRights(group),
            Links =
            [
                new Link("self", $"/api/groups/{group.Id}", "GET"),
                new Link("rights", $"/api/groups/{group.Id}/rights", "PUT"),
                // The group's membership, and removing the group itself (issue #416).
                new Link("members", $"/api/groups/{group.Id}/members", "GET"),
                new Link("delete", $"/api/groups/{group.Id}", "DELETE"),
            ],
        };
    }

    private static SystemRights ReadRights(Group g) => new()
    {
        IsTenantAdmin = g.IsTenantAdmin,
        CanImpersonate = g.CanImpersonate,
        CanOverrideCheckout = g.CanOverrideCheckout,
        CanLegalHold = g.CanLegalHold,
        CanManageClassification = g.CanManageClassification,
        CanResetMfa = g.CanResetMfa,
        CanManageRepositories = g.CanManageRepositories,
        CanManageMasks = g.CanManageMasks,
        CanManageServiceAccounts = g.CanManageServiceAccounts,
        CanManageUsers = g.CanManageUsers,
        CanViewAuditLog = g.CanViewAuditLog,
        CanExport = g.CanExport,
        CanImport = g.CanImport,
        CanManageInboxes = g.CanManageInboxes,
        CanCreateExternalLink = g.CanCreateExternalLink,
        ClearanceRank = g.ClearanceRank,
    };

    private static void ApplyRights(Group g, SystemRights r)
    {
        g.IsTenantAdmin = r.IsTenantAdmin;
        g.CanImpersonate = r.CanImpersonate;
        g.CanOverrideCheckout = r.CanOverrideCheckout;
        g.CanLegalHold = r.CanLegalHold;
        g.CanManageClassification = r.CanManageClassification;
        g.CanResetMfa = r.CanResetMfa;
        g.CanManageRepositories = r.CanManageRepositories;
        g.CanManageMasks = r.CanManageMasks;
        g.CanManageServiceAccounts = r.CanManageServiceAccounts;
        g.CanManageUsers = r.CanManageUsers;
        g.CanViewAuditLog = r.CanViewAuditLog;
        g.CanExport = r.CanExport;
        g.CanImport = r.CanImport;
        g.CanManageInboxes = r.CanManageInboxes;
        g.CanCreateExternalLink = r.CanCreateExternalLink;
        g.ClearanceRank = r.ClearanceRank;
    }

    // The caller's own system rights, for the escalation cap — a ServiceAccount only carries the four
    // management rights, a User carries all ten.
    private async Task<SystemRights> GetCallerSystemRightsAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            var sa = await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => new { s.CanManageRepositories, s.CanManageMasks, s.CanManageServiceAccounts, s.CanManageUsers, s.CanViewAuditLog, s.CanExport, s.CanImport, s.CanManageInboxes })
                .SingleAsync(cancellationToken);

            return new SystemRights
            {
                CanManageRepositories = sa.CanManageRepositories,
                CanManageMasks = sa.CanManageMasks,
                CanManageServiceAccounts = sa.CanManageServiceAccounts,
                CanManageUsers = sa.CanManageUsers,
                CanViewAuditLog = sa.CanViewAuditLog,
                CanExport = sa.CanExport,
                CanImport = sa.CanImport,
                CanManageInboxes = sa.CanManageInboxes,
                ClearanceRank = (await _clearanceResolver.GetForServiceAccountAsync(serviceAccountId, cancellationToken)).Rank,
            };
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            // Effective rights (own ∪ groups) so the escalation cap counts rights held via a group as
            // grantable — ADR "Enforce group system rights for members".
            var r = await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken);
            return new SystemRights
            {
                IsTenantAdmin = r.IsTenantAdmin,
                CanImpersonate = r.CanImpersonate,
                CanOverrideCheckout = r.CanOverrideCheckout,
                CanLegalHold = r.CanLegalHold,
                CanManageClassification = r.CanManageClassification,
                CanResetMfa = r.CanResetMfa,
                CanManageRepositories = r.CanManageRepositories,
                CanManageMasks = r.CanManageMasks,
                CanManageServiceAccounts = r.CanManageServiceAccounts,
                CanManageUsers = r.CanManageUsers,
                CanViewAuditLog = r.CanViewAuditLog,
                CanExport = r.CanExport,
                CanImport = r.CanImport,
                CanManageInboxes = r.CanManageInboxes,
                CanCreateExternalLink = r.CanCreateExternalLink,
                ClearanceRank = (await _clearanceResolver.GetForUserAsync(userId, cancellationToken)).Rank,
            };
        }

        return new SystemRights();
    }

    // Checks ServiceAccount.CanManageUsers first, then User.CanManageUsers — see ADR "User support for
    // ServiceAccount/User/Group/Mask management endpoints".
    private async Task<bool> CanManageUsersAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanManageUsers)
                .SingleAsync(cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            // Effective rights (own ∪ groups) so CanManageUsers held via a group takes effect — ADR
            // "Enforce group system rights for members".
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageUsers;
        }

        return false;
    }
}
