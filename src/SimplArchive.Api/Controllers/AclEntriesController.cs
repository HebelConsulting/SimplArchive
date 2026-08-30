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
using SimplArchive.Domain.Documents;
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

    // The picker catalog for the Manage-access dialog (ADR "Manage-access UI for document/folder ACLs").
    public class GrantablePrincipalsResource : HypermediaResource
    {
        public List<GrantablePrincipal> Principals { get; set; } = [];
    }

    public class GrantablePrincipal : HypermediaResource
    {
        public string Type { get; set; } = "";   // users | groups | service-accounts
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class AclEntriesListResource : HypermediaResource
    {
        public List<AclEntryResource> Entries { get; set; } = [];

        /// <summary>Which rights THIS caller may grant on THIS document (#877).</summary>
        /// <remarks>
        /// <para>
        /// <c>EffectiveRights.Covers</c> caps a grant at the caller's own effective rights and answers a
        /// violation with <c>403 INSUFFICIENT_RIGHTS_TO_GRANT</c> — but the API advertised no cap, so the
        /// manage-access dialog offered all nine rights uncapped and learned otherwise on save. Unlike the rest
        /// of this epic that could not be fixed in the client: the answer was not on the wire at all.
        /// </para>
        /// <para>
        /// Per DOCUMENT rather than per row, because that is the scope <c>Covers</c> works at: the cap is the
        /// caller's rights on this item, identical for every principal being granted and for a new grant that
        /// has no row yet.
        /// </para>
        /// <para>
        /// It costs nothing: <c>List</c> already resolves these rights to authorize itself, so the advertised
        /// cap is the same value the enforcement uses rather than a second computation that agrees (ADR 0722).
        /// </para>
        /// </remarks>
        public GrantableAclRights GrantableRights { get; set; } = new();
    }

    /// <summary>The nine document rights, as booleans the caller may confer (#877).</summary>
    public class GrantableAclRights
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

    public class SetInheritanceRequest
    {
        public bool BreaksInheritance { get; set; }
    }

    public class InheritanceResource : HypermediaResource
    {
        public bool BreaksInheritance { get; set; }
    }

    // The resolved "who can actually access this" view (ADR 0488): the effective grants (from the governing
    // scope) plus each granted group expanded to the individual users it confers access on, plus tenant admins.
    public class EffectiveAccessResource : HypermediaResource
    {
        // The folder path the grants are inherited from, or null when they're set directly on this item.
        public string? InheritedFrom { get; set; }

        public List<EffectiveAccessEntry> Entries { get; set; } = [];
    }

    public class EffectiveAccessEntry
    {
        public string Type { get; set; } = "";     // users | groups | service-accounts
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Access { get; set; } = "";    // direct | group | admin
        public string? ViaGroup { get; set; }        // the group name when Access == "group"
        public bool CanSee { get; set; }
        public bool CanReadContent { get; set; }
        public bool CanEditContent { get; set; }
        public bool CanEditIndexData { get; set; }
        public bool CanCreateSubItems { get; set; }
        public bool CanDelete { get; set; }
        public bool CanMove { get; set; }
        public bool CanAnnotate { get; set; }
        public bool CanManagePermissions { get; set; }
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

        // Resolved ONCE and used twice: to authorize this read, and as the cap advertised below (#877). The
        // alternative — gate here, recompute there — is the pattern ADR 0722 forbids.
        var callerRights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (callerRights is not { CanManagePermissions: true })
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

        // Everything the manage-access dialog needs hangs off the collection it has just opened (issue #416):
        // the principals it may grant to, the resolved effective view, and the address a grant is written to.
        // Granting to someone NEW has no address until a principal is chosen, so that rel rides on each
        // grantable principal below rather than on this collection.
        var links = new List<Link>
        {
            new("self", Url.Action(nameof(List), new { documentId, cursor, limit = pageSize })!, "GET"),
            new("grantable-principals", Url.Action(nameof(GrantablePrincipals), new { documentId })!, "GET"),
            new("effective", Url.Action(nameof(Effective), new { documentId })!, "GET"),
        };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new AclEntriesListResource
        {
            Entries = page.Select(BuildResource).ToList(),
            GrantableRights = new GrantableAclRights
            {
                CanSee = callerRights.CanSee,
                CanReadContent = callerRights.CanReadContent,
                CanEditContent = callerRights.CanEditContent,
                CanEditIndexData = callerRights.CanEditIndexData,
                CanDelete = callerRights.CanDelete,
                CanCreateSubItems = callerRights.CanCreateSubItems,
                CanManagePermissions = callerRights.CanManagePermissions,
                CanMove = callerRights.CanMove,
                CanAnnotate = callerRights.CanAnnotate,
            },
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

    // The users/groups/service-accounts a manager can grant to (ADR "Manage-access UI for document/folder ACLs")
    // — gated on CanManagePermissions on THIS document (not CanManageUsers), so a permissions manager who isn't a
    // user-admin can still populate the picker (same reasoning as assignable-reviewers). Bounded, not paginated.
    [HttpGet("grantable-principals")]
    public async Task<IActionResult> GrantablePrincipals(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var groups = await _dbContext.Groups.OrderBy(g => g.Name)
            .Select(g => new GrantablePrincipal { Type = "groups", Id = g.Id, Name = g.Name })
            .ToListAsync(cancellationToken);
        var users = await _dbContext.Users.Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new GrantablePrincipal { Type = "users", Id = u.Id, Name = u.DisplayName })
            .ToListAsync(cancellationToken);
        var serviceAccounts = await _dbContext.ServiceAccounts.Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new GrantablePrincipal { Type = "service-accounts", Id = s.Id, Name = s.Name })
            .ToListAsync(cancellationToken);

        var principals = new List<GrantablePrincipal>([.. groups, .. users, .. serviceAccounts]);

        // The address at which a grant FOR THIS PRINCIPAL is written (issue #416). A new grant has no resource
        // yet, so there is nothing else that could carry its address — putting it on the picker's own rows is
        // what lets the dialog save without composing /acl-entries/{type}/{id} from the selection.
        foreach (var principal in principals)
        {
            principal.Links = [new Link("grant", $"/api/documents/{documentId}/acl-entries/{principal.Type}/{principal.Id}", "PUT")];
        }

        return Ok(new GrantablePrincipalsResource { Principals = principals });
    }

    [HttpHead("grantable-principals")]
    public async Task<IActionResult> HeadGrantablePrincipals(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return await CanManagePermissionsAsync(documentId, cancellationToken) ? NoContent() : Forbid();
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

        // A grant to a User notifies them of the new access (group / service-account grants have no intray).
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

    // Break or restore ACL inheritance on a document/folder (ADR "Manage-access UI ..." inheritance follow-up).
    //   Break (true): snapshot the currently-effective inherited grants as explicit own grants on this document
    //     (so access is preserved and then editable), then set the flag. No escalation cap — the copied grants
    //     already applied via inheritance, so copying them down grants nobody anything new.
    //   Restore (false): discard this document's own grants and revert to inheriting from the parent.
    // A repository root has no parent to inherit from (its own grants are always the fallback), so the toggle is
    // rejected there. Gated on the caller's own CanManagePermissions, like every action here.
    [HttpPut("inheritance")]
    public async Task<IActionResult> SetInheritance(Guid documentId, [FromBody] SetInheritanceRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (document.ParentId is null)
        {
            throw new CannotChangeRootInheritanceException();
        }

        if (document.BreaksInheritance == request.BreaksInheritance)
        {
            return Ok(new InheritanceResource { BreaksInheritance = document.BreaksInheritance });
        }

        if (request.BreaksInheritance)
        {
            // Copy the governing scope's own grants down so effective access is preserved, then break.
            var governingScopeId = await ResolveGoverningScopeAsync(document.ParentId, cancellationToken);
            if (governingScopeId is { } sourceId)
            {
                var sourceEntries = await _dbContext.AclEntries.Where(a => a.DocumentId == sourceId).ToListAsync(cancellationToken);
                foreach (var src in sourceEntries)
                {
                    _dbContext.AclEntries.Add(new AclEntry
                    {
                        Id = Guid.NewGuid(),
                        TenantId = document.TenantId,
                        DocumentId = documentId,
                        UserId = src.UserId,
                        GroupId = src.GroupId,
                        ServiceAccountId = src.ServiceAccountId,
                        CanSee = src.CanSee,
                        CanReadContent = src.CanReadContent,
                        CanEditContent = src.CanEditContent,
                        CanEditIndexData = src.CanEditIndexData,
                        CanDelete = src.CanDelete,
                        CanCreateSubItems = src.CanCreateSubItems,
                        CanManagePermissions = src.CanManagePermissions,
                        CanMove = src.CanMove,
                        CanAnnotate = src.CanAnnotate,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }

            document.BreaksInheritance = true;
        }
        else
        {
            // Restore: discard own grants and revert to inheriting from the parent.
            var ownEntries = await _dbContext.AclEntries.Where(a => a.DocumentId == documentId).ToListAsync(cancellationToken);
            _dbContext.AclEntries.RemoveRange(ownEntries);
            document.BreaksInheritance = false;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Changing inheritance changes the resolved visibility of this document and its inheriting descendants.
        await EnqueueSubtreeAsync(documentId, cancellationToken);

        await _audit.RecordAsync(
            request.BreaksInheritance ? AuditActions.AclInheritanceBroken : AuditActions.AclInheritanceRestored,
            "Document", documentId, document.Name, cancellationToken: cancellationToken);

        return Ok(new InheritanceResource { BreaksInheritance = document.BreaksInheritance });
    }

    // The resolved "who can actually access this" view (ADR 0488): the effective grants (from the governing
    // scope) resolved to people — each granted group expanded to the users it confers access on, plus tenant
    // admins (who bypass the ACL). Read-only, gated on the caller's own CanManagePermissions like the rest.
    [HttpGet("effective")]
    public async Task<IActionResult> Effective(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Id, d.ParentId, d.BreaksInheritance })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // The governing scope: this document's own grants if it breaks inheritance, else the nearest breaking
        // ancestor / root (whose grants it inherits).
        Guid? scopeId = document.BreaksInheritance
            ? document.Id
            : await ResolveGoverningScopeAsync(document.ParentId, cancellationToken);

        var inheritedFrom = scopeId is { } sid && sid != documentId
            ? await BuildPathAsync(sid, cancellationToken)
            : null;

        var grants = scopeId is { } gid
            ? await _dbContext.AclEntries.Where(a => a.DocumentId == gid).ToListAsync(cancellationToken)
            : [];

        var entries = new List<EffectiveAccessEntry>();

        // Per-user accumulator: union of rights + the highest-precedence source (admin > direct > group).
        var users = new Dictionary<Guid, UserAccess>();

        void MergeUser(Guid userId, AclEntry rights, string access, string? viaGroup)
        {
            if (!users.TryGetValue(userId, out var acc))
            {
                acc = new UserAccess();
                users[userId] = acc;
            }

            acc.Rights.CanSee |= rights.CanSee;
            acc.Rights.CanReadContent |= rights.CanReadContent;
            acc.Rights.CanEditContent |= rights.CanEditContent;
            acc.Rights.CanEditIndexData |= rights.CanEditIndexData;
            acc.Rights.CanCreateSubItems |= rights.CanCreateSubItems;
            acc.Rights.CanDelete |= rights.CanDelete;
            acc.Rights.CanMove |= rights.CanMove;
            acc.Rights.CanAnnotate |= rights.CanAnnotate;
            acc.Rights.CanManagePermissions |= rights.CanManagePermissions;
            // Precedence: admin (2) > direct (1) > group (0). Keep the strongest source label.
            var rank = access switch { "admin" => 2, "direct" => 1, _ => 0 };
            if (rank >= acc.Rank)
            {
                acc.Rank = rank;
                acc.Access = access;
                acc.ViaGroup = viaGroup;
            }
        }

        foreach (var grant in grants)
        {
            if (grant.UserId is { } directUserId)
            {
                MergeUser(directUserId, grant, "direct", null);
            }
            else if (grant.ServiceAccountId is { } saId)
            {
                var saName = await _dbContext.ServiceAccounts.Where(s => s.Id == saId).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken);
                entries.Add(BuildEntry("service-accounts", saId, saName ?? "", "direct", null, grant));
            }
            else if (grant.GroupId is { } groupId)
            {
                var groupName = await _dbContext.Groups.Where(g => g.Id == groupId).Select(g => g.Name).SingleOrDefaultAsync(cancellationToken) ?? "";
                entries.Add(BuildEntry("groups", groupId, groupName, "direct", null, grant));

                // Membership flows down: a grant on group G reaches every user who is a member of G or any of
                // its ancestors (ADR "Document ACL inheritance resolution" / group expansion).
                foreach (var memberId in await UsersEffectivelyInGroupAsync(groupId, cancellationToken))
                {
                    MergeUser(memberId, grant, "group", groupName);
                }
            }
        }

        // Tenant admins bypass the ACL entirely — full rights, however the grants fall.
        var full = new AclEntry
        {
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanCreateSubItems = true,
            CanDelete = true,
            CanMove = true,
            CanAnnotate = true,
            CanManagePermissions = true,
        };
        foreach (var adminId in await TenantAdminUserIdsAsync(cancellationToken))
        {
            MergeUser(adminId, full, "admin", null);
        }

        // Resolve the accumulated users to entries (active users only — a deactivated user has no rights).
        var userIds = users.Keys.ToList();
        var userRows = await _dbContext.Users
            .Where(u => userIds.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken);

        foreach (var u in userRows)
        {
            var acc = users[u.Id];
            entries.Add(BuildEntry("users", u.Id, u.DisplayName, acc.Access, acc.ViaGroup, acc.Rights));
        }

        return Ok(new EffectiveAccessResource { InheritedFrom = inheritedFrom, Entries = entries });
    }

    [HttpHead("effective")]
    public async Task<IActionResult> HeadEffective(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return await CanManagePermissionsAsync(documentId, cancellationToken) ? NoContent() : Forbid();
    }

    private sealed class UserAccess
    {
        public AclEntry Rights { get; } = new();
        public int Rank { get; set; } = -1;
        public string Access { get; set; } = "";
        public string? ViaGroup { get; set; }
    }

    private static EffectiveAccessEntry BuildEntry(string type, Guid id, string name, string access, string? viaGroup, AclEntry r) => new()
    {
        Type = type,
        Id = id,
        Name = name,
        Access = access,
        ViaGroup = viaGroup,
        CanSee = r.CanSee,
        CanReadContent = r.CanReadContent,
        CanEditContent = r.CanEditContent,
        CanEditIndexData = r.CanEditIndexData,
        CanCreateSubItems = r.CanCreateSubItems,
        CanDelete = r.CanDelete,
        CanMove = r.CanMove,
        CanAnnotate = r.CanAnnotate,
        CanManagePermissions = r.CanManagePermissions,
    };

    // Users who effectively belong to a group: direct members of it or any of its ancestors (membership flows
    // down). One query per ancestor level, then a single membership query.
    private async Task<List<Guid>> UsersEffectivelyInGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var groupIds = new List<Guid> { groupId };
        var currentId = (Guid?)groupId;

        while (currentId is { } id)
        {
            var parentId = await _dbContext.Groups.Where(g => g.Id == id).Select(g => g.ParentGroupId).SingleOrDefaultAsync(cancellationToken);
            if (parentId is { } pid && !groupIds.Contains(pid))
            {
                groupIds.Add(pid);
                currentId = pid;
            }
            else
            {
                currentId = null;
            }
        }

        return await _dbContext.GroupMemberships
            .Where(m => groupIds.Contains(m.GroupId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    // All users who effectively hold IsTenantAdmin: own flag, or membership of any IsTenantAdmin group.
    private async Task<HashSet<Guid>> TenantAdminUserIdsAsync(CancellationToken cancellationToken)
    {
        var admins = new HashSet<Guid>(await _dbContext.Users.Where(u => u.IsTenantAdmin).Select(u => u.Id).ToListAsync(cancellationToken));

        var adminGroupIds = await _dbContext.Groups.Where(g => g.IsTenantAdmin).Select(g => g.Id).ToListAsync(cancellationToken);
        foreach (var groupId in adminGroupIds)
        {
            foreach (var memberId in await UsersEffectivelyInGroupAsync(groupId, cancellationToken))
            {
                admins.Add(memberId);
            }
        }

        return admins;
    }

    // A document's folder path ("Repositories / Contracts / 2026"), walking up ParentId.
    private async Task<string> BuildPathAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var currentId = (Guid?)documentId;

        while (currentId is { } id)
        {
            var node = await _dbContext.Documents.Where(d => d.Id == id).Select(d => new { d.Name, d.ParentId }).SingleOrDefaultAsync(cancellationToken);
            if (node is null)
            {
                break;
            }

            names.Add(node.Name);
            currentId = node.ParentId;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }

    // The ACL scope a currently-inheriting document draws its grants from: the nearest ancestor that itself
    // breaks inheritance, else the repository root (whose own grants are the ultimate fallback). Mirrors the
    // resolution in EffectiveRightsCalculator (ADR "Document ACL inheritance resolution") — one query per
    // ancestor level, walking up from the parent.
    private async Task<Guid?> ResolveGoverningScopeAsync(Guid? parentId, CancellationToken cancellationToken)
    {
        var currentId = parentId;
        Guid? rootId = null;

        while (currentId is { } id)
        {
            var node = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => new { d.Id, d.ParentId, d.BreaksInheritance })
                .SingleOrDefaultAsync(cancellationToken);

            if (node is null)
            {
                break;
            }

            if (node.BreaksInheritance)
            {
                return node.Id;
            }

            rootId = node.Id;
            currentId = node.ParentId;
        }

        return rootId;
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
            // The grant's own address, advertised ONCE: read it with GET, replace it with PUT, remove it with
            // DELETE (ADR 0719). `edit` and `remove` were the same URL under two more names, and the method
            // already said which was which. Reaching this collection at all requires CanManagePermissions, so
            // there is no narrower right for a capability flag to carry here.
            //
            // The `grant` on a PRINCIPAL row is deliberately NOT folded in: it is emitted only for a principal
            // that has no entry yet, so its presence names an available transition rather than a verb.
            Links =
            [
                new Link("self", $"/api/documents/{entry.DocumentId}/acl-entries/{principalType}/{principalId}", "GET"),
            ],
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
