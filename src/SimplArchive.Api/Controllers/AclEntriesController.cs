using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Authorization;
using SimplArchive.Api.Errors.Exceptions.Acl;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "ACL grant management endpoints" — creates/lists/revokes AclEntry grants on a Document.
/// Keyed by principal (users/groups/service-accounts), not by AclEntry.Id, so PUT can idempotently set a
/// principal's complete rights bundle without a client first having to look up an opaque row id. Every
/// action requires the caller's own CanManagePermissions on the target document; PUT additionally enforces
/// EffectiveRights.Covers as an escalation cap (DELETE does not, since revoking can't escalate privilege).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/acl-entries")]
[Authorize]
public class AclEntriesController : ControllerBase
{
    private static readonly HashSet<string> ValidPrincipalTypes = ["users", "groups", "service-accounts"];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IDocumentIndexQueue _indexQueue;
    private readonly IAuditRecorder _audit;
    private readonly INotificationService _notifications;

    public AclEntriesController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IDocumentIndexQueue indexQueue,
        IAuditRecorder audit,
        INotificationService notifications)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _indexQueue = indexQueue;
        _audit = audit;
        _notifications = notifications;
    }

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class AclEntryResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string PrincipalType { get; set; } = "";

        public Guid PrincipalId { get; set; }

        public bool CanSee { get; set; }

        public bool CanReadContent { get; set; }

        public bool CanEditContent { get; set; }

        public bool CanEditIndexData { get; set; }

        public bool CanDelete { get; set; }

        public bool CanCreateSubItems { get; set; }

        public bool CanManagePermissions { get; set; }

        public bool CanMove { get; set; }

        public bool CanAnnotate { get; set; }
    }

    public class AclEntriesListResource : HypermediaResource
    {
        public List<AclEntryResource> Entries { get; set; } = [];
    }

    public class SetAclEntryRequest
    {
        public bool CanSee { get; set; }

        public bool CanReadContent { get; set; }

        public bool CanEditContent { get; set; }

        public bool CanEditIndexData { get; set; }

        public bool CanDelete { get; set; }

        public bool CanCreateSubItems { get; set; }

        public bool CanManagePermissions { get; set; }

        public bool CanMove { get; set; }

        public bool CanAnnotate { get; set; }
    }

    // Lists only the AclEntry rows directly on this document — not the resolved/inherited effective view
    // (ADR "Document ACL inheritance resolution"). A document that doesn't BreaksInheritance correctly
    // shows an empty list here even though it has effective rights via an ancestor.
    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet]
    public async Task<IActionResult> List(Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.AclEntries.Where(a => a.DocumentId == documentId);

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(a => a.CreatedAt > cursorCreatedAt || (a.CreatedAt == cursorCreatedAt && a.Id > cursorId));
        }

        var fetched = await query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { documentId, cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new AclEntriesListResource
        {
            Entries = page.Select(BuildResource).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public async Task<IActionResult> HeadList(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpGet("{principalType}/{principalId:guid}")]
    public async Task<IActionResult> Get(Guid documentId, string principalType, Guid principalId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (!ValidPrincipalTypes.Contains(principalType))
        {
            throw new InvalidPrincipalTypeException(principalType);
        }

        var entry = await QueryByPrincipal(documentId, principalType, principalId).SingleOrDefaultAsync(cancellationToken);

        if (entry is null)
        {
            return NotFound();
        }

        return Ok(BuildResource(entry));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{principalType}/{principalId:guid}")]
    public async Task<IActionResult> Head(Guid documentId, string principalType, Guid principalId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (!ValidPrincipalTypes.Contains(principalType))
        {
            throw new InvalidPrincipalTypeException(principalType);
        }

        var exists = await QueryByPrincipal(documentId, principalType, principalId).AnyAsync(cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    // PUT, not POST-create/DELETE-by-id — AclEntry's own unique-index shape already forbids two rows for
    // the same (DocumentId, principal), so keying by principal and replacing in place sidesteps having to
    // look up an opaque AclEntry.Id first. See ADR "ACL grant management endpoints".
    [HttpPut("{principalType}/{principalId:guid}")]
    public async Task<IActionResult> Set(Guid documentId, string principalType, Guid principalId, [FromBody] SetAclEntryRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var callerRights = await GetCallerRightsAsync(documentId, cancellationToken);

        if (callerRights is null || !callerRights.CanManagePermissions)
        {
            return Forbid();
        }

        if (!ValidPrincipalTypes.Contains(principalType))
        {
            throw new InvalidPrincipalTypeException(principalType);
        }

        if (!(request.CanSee || request.CanReadContent || request.CanEditContent || request.CanEditIndexData
            || request.CanDelete || request.CanCreateSubItems || request.CanManagePermissions || request.CanMove || request.CanAnnotate))
        {
            throw new EmptyGrantNotAllowedException();
        }

        if (!await PrincipalExistsAsync(principalType, principalId, cancellationToken))
        {
            return NotFound();
        }

        var entry = await QueryByPrincipal(documentId, principalType, principalId).SingleOrDefaultAsync(cancellationToken);

        if (entry is null)
        {
            entry = new AclEntry { Id = Guid.NewGuid(), TenantId = document.TenantId, DocumentId = documentId, CreatedAt = DateTimeOffset.UtcNow };
            AssignPrincipal(entry, principalType, principalId);
            _dbContext.AclEntries.Add(entry);
        }

        entry.CanSee = request.CanSee;
        entry.CanReadContent = request.CanReadContent;
        entry.CanEditContent = request.CanEditContent;
        entry.CanEditIndexData = request.CanEditIndexData;
        entry.CanDelete = request.CanDelete;
        entry.CanCreateSubItems = request.CanCreateSubItems;
        entry.CanManagePermissions = request.CanManagePermissions;
        entry.CanMove = request.CanMove;
        entry.CanAnnotate = request.CanAnnotate;

        // See ADR "ACL management right": a CanManagePermissions holder can only grant rights that are a
        // subset of their own current effective rights on this document.
        if (!callerRights.Covers(entry))
        {
            throw InsufficientRightsToGrantException.OnDocument();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // The grant changed this document's (and its inheriting descendants') indexed visibility — reindex
        // the subtree (ADR "Indexed ACL in search").
        await EnqueueSubtreeAsync(documentId, cancellationToken);

        var docName = await DocumentNameAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.AclGranted, "Document", documentId, docName, $"{principalType} {principalId}: {DescribeAclRights(entry)}", cancellationToken: cancellationToken);

        // A grant to a User notifies them of the new access (group / service-account grants have no inbox).
        if (principalType == "users")
        {
            await _notifications.NotifyAsync(principalId, NotificationType.AccessGranted, "Access granted", $"You were granted access to '{docName}'.", documentId, cancellationToken);
        }

        return Ok(BuildResource(entry));
    }

    // No escalation cap here, unlike Set — revoking only ever removes access, so it can't be used to
    // escalate privilege. See ADR "ACL grant management endpoints".
    [HttpDelete("{principalType}/{principalId:guid}")]
    public async Task<IActionResult> Revoke(Guid documentId, string principalType, Guid principalId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (!ValidPrincipalTypes.Contains(principalType))
        {
            throw new InvalidPrincipalTypeException(principalType);
        }

        var entry = await QueryByPrincipal(documentId, principalType, principalId).SingleOrDefaultAsync(cancellationToken);

        if (entry is null)
        {
            return NotFound();
        }

        _dbContext.AclEntries.Remove(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await EnqueueSubtreeAsync(documentId, cancellationToken);

        await _audit.RecordAsync(AuditActions.AclRevoked, "Document", documentId, await DocumentNameAsync(documentId, cancellationToken), $"{principalType} {principalId}", cancellationToken: cancellationToken);

        return NoContent();
    }

    private Task<string?> DocumentNameAsync(Guid documentId, CancellationToken cancellationToken) =>
        _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken);

    private static string DescribeAclRights(AclEntry e)
    {
        var names = new List<string>();
        if (e.CanSee) names.Add("See");
        if (e.CanReadContent) names.Add("ReadContent");
        if (e.CanEditContent) names.Add("EditContent");
        if (e.CanEditIndexData) names.Add("EditIndexData");
        if (e.CanDelete) names.Add("Delete");
        if (e.CanCreateSubItems) names.Add("CreateSubItems");
        if (e.CanManagePermissions) names.Add("ManagePermissions");
        if (e.CanMove) names.Add("Move");
        if (e.CanAnnotate) names.Add("Annotate");
        return string.Join(", ", names);
    }

    // Enqueues the document and every descendant for reindexing after an ACL change (indexed-ACL, ADR
    // "Indexed ACL in search"): a grant on this document changes the resolved visibility of every document
    // that inherits from it. Whole-subtree (over-inclusive but idempotent), the same iterative level-by-level
    // traversal as the delete-cascade. Only live documents are collected (query filters exclude soft-deleted
    // ones — already absent from the index).
    private async Task EnqueueSubtreeAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var ids = new List<Guid> { rootId };
        var frontier = new List<Guid> { rootId };

        while (frontier.Count > 0)
        {
            var children = await _dbContext.Documents
                .Where(d => d.ParentId != null && frontier.Contains(d.ParentId.Value))
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            ids.AddRange(children);
            frontier = children;
        }

        await _indexQueue.EnqueueManyAsync(ids, cancellationToken);
    }

    private IQueryable<AclEntry> QueryByPrincipal(Guid documentId, string principalType, Guid principalId)
    {
        return principalType switch
        {
            "users" => _dbContext.AclEntries.Where(a => a.DocumentId == documentId && a.UserId == principalId),
            "groups" => _dbContext.AclEntries.Where(a => a.DocumentId == documentId && a.GroupId == principalId),
            "service-accounts" => _dbContext.AclEntries.Where(a => a.DocumentId == documentId && a.ServiceAccountId == principalId),
            _ => _dbContext.AclEntries.Where(_ => false),
        };
    }

    private async Task<bool> PrincipalExistsAsync(string principalType, Guid principalId, CancellationToken cancellationToken)
    {
        return principalType switch
        {
            "users" => await _dbContext.Users.AnyAsync(u => u.Id == principalId, cancellationToken),
            "groups" => await _dbContext.Groups.AnyAsync(g => g.Id == principalId, cancellationToken),
            "service-accounts" => await _dbContext.ServiceAccounts.AnyAsync(s => s.Id == principalId, cancellationToken),
            _ => false,
        };
    }

    private static void AssignPrincipal(AclEntry entry, string principalType, Guid principalId)
    {
        switch (principalType)
        {
            case "users":
                entry.UserId = principalId;
                break;
            case "groups":
                entry.GroupId = principalId;
                break;
            case "service-accounts":
                entry.ServiceAccountId = principalId;
                break;
        }
    }

    private static AclEntryResource BuildResource(AclEntry entry)
    {
        var (principalType, principalId) = entry switch
        {
            { UserId: { } userId } => ("users", userId),
            { GroupId: { } groupId } => ("groups", groupId),
            { ServiceAccountId: { } serviceAccountId } => ("service-accounts", serviceAccountId),
            _ => throw new InvalidOperationException("AclEntry has no principal set."),
        };

        return new AclEntryResource
        {
            Id = entry.Id,
            PrincipalType = principalType,
            PrincipalId = principalId,
            CanSee = entry.CanSee,
            CanReadContent = entry.CanReadContent,
            CanEditContent = entry.CanEditContent,
            CanEditIndexData = entry.CanEditIndexData,
            CanDelete = entry.CanDelete,
            CanCreateSubItems = entry.CanCreateSubItems,
            CanManagePermissions = entry.CanManagePermissions,
            CanMove = entry.CanMove,
            CanAnnotate = entry.CanAnnotate,
            Links = [new Link("self", $"/api/documents/{entry.DocumentId}/acl-entries/{principalType}/{principalId}", "GET")],
        };
    }

    // Checks ServiceAccount first, then a logged-in User — the two accessors are mutually exclusive per
    // request (CurrentPrincipalMiddleware's three-way branch). See ADR "Document-scope authorization
    // retrofit for User, and tenant-administrator-driven onboarding".
    private async Task<EffectiveRights?> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken);
        }

        return null;
    }

    private async Task<bool> CanManagePermissionsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var rights = await GetCallerRightsAsync(documentId, cancellationToken);

        return rights?.CanManagePermissions ?? false;
    }
}
