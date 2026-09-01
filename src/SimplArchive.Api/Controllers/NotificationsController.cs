using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Notifications;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's in-app notification intray (ADR "Notifications (in-app, first slice)"). Every User sees only
/// their own notifications — no special right; a ServiceAccount / PlatformAdministrator has no intray. Written
/// by <see cref="INotificationService"/> at the workflow / comment / ACL trigger sites; read here.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public NotificationsController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor, ICurrentTenantAccessor currentTenantAccessor)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
    }

    public class NotificationResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        // How many coalesced events this notification represents (ADR "Notification digest / coalescing"); 1 for a
        // normal one. The clients render "… (×N)" when > 1.
        public int EventCount { get; set; } = 1;
        public Guid? DocumentId { get; set; }

        // The related document's parent folder (ADR "Notification viewer + click-through") — lets a client
        // navigate to the document (open the folder + select it). Null when the notification has no document, or
        // the document is a repository root.
        public Guid? DocumentParentId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ReadAt { get; set; }
        public bool IsRead { get; set; }
    }

    public class NotificationsListResource : HypermediaResource
    {
        public List<NotificationResource> Notifications { get; set; } = [];
        public int UnreadCount { get; set; }
    }

    public class UnreadCountResource : HypermediaResource
    {
        public int UnreadCount { get; set; }
    }

    public class PreferenceResource
    {
        public int Type { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public bool EmailEnabled { get; set; }
    }

    public class PreferencesResource : HypermediaResource
    {
        public List<PreferenceResource> Preferences { get; set; } = [];
    }

    public class SetPreferencesRequest
    {
        public List<PreferenceItem>? Preferences { get; set; }
    }

    public class PreferenceItem
    {
        public int Type { get; set; }
        public bool EmailEnabled { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);
        var query = _dbContext.Notifications.Where(n => n.RecipientUserId == userId);

        // Newest first; the cursor is a (CreatedAt, Id) position, so "next" = strictly older.
        var pageQuery = query;
        if (Cursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            pageQuery = pageQuery.Where(n => n.CreatedAt < cursorTimestamp || (n.CreatedAt == cursorTimestamp && n.Id < cursorId));
        }

        var fetched = await pageQuery
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        // Resolve each document notification's parent folder in one query, so a client can navigate to it.
        var documentIds = page.Where(n => n.DocumentId is not null).Select(n => n.DocumentId!.Value).Distinct().ToList();
        var parents = documentIds.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : await _dbContext.Documents.Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.ParentId, cancellationToken);

        var unreadCount = await query.CountAsync(n => n.ReadAt == null, cancellationToken);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET"),
            new("preferences", Url.Action(nameof(GetPreferences))!, "GET"),
            new("unread-count", Url.Action(nameof(UnreadCount))!, "GET"),
        };

        // "Mark everything read" — advertised only when something is actually unread, so the client can grey the
        // affordance out from the rel instead of offering an action whose only effect would be a round trip.
        // Gated on the WHOLE unread count, not this page's: an unread notification further down the cursor is
        // still something read-all would clear, and hiding the rel there would make the button lie.
        if (unreadCount > 0)
        {
            links.Add(new Link("read-all", Url.Action(nameof(MarkAllRead))!, "POST"));
        }
        if (hasMore)
        {
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id), limit = pageSize })!, "GET"));
        }

        return Ok(new NotificationsListResource
        {
            Notifications = page.Select(n => BuildResource(n, parents)).ToList(),
            UnreadCount = unreadCount,
            Links = links,
        });
    }

    [HttpHead]
    public IActionResult Head() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();

    // Just the unread count — the client polls this cheaply for the bell badge.
    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        return Ok(new UnreadCountResource
        {
            UnreadCount = await _dbContext.Notifications.CountAsync(n => n.RecipientUserId == userId && n.ReadAt == null, cancellationToken),
            Links = [new Link("self", Url.Action(nameof(UnreadCount))!, "GET")],
        });
    }

    [HttpHead("unread-count")]
    public IActionResult UnreadCountHead() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();

    // Marks one of the caller's notifications read (idempotent). A POST action sub-resource — a state change,
    // not a create/replace.
    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var notification = await _dbContext.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.RecipientUserId == userId, cancellationToken);
        if (notification is null)
        {
            return NotFound();
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    // Marks all of the caller's unread notifications read.
    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        await _dbContext.Notifications
            .Where(n => n.RecipientUserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), cancellationToken);

        return NoContent();
    }

    // ---- Email preferences (ADR "Notification preferences") ---------------------------------------------
    // In-app notifications are always delivered; these govern only whether a type is also emailed. Only the
    // mutable types (NotificationTypePolicy) are listed/settable — the deadline/compliance escalations are
    // always emailed. Absence of a row means the default (enabled), so this returns true for any unset type.

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        return Ok(await BuildPreferencesAsync(userId, cancellationToken));
    }

    [HttpHead("preferences")]
    public IActionResult PreferencesHead() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();

    // Replaces the caller's whole preference set. PUT (not PATCH): the body is the complete intended state of
    // the mutable-type email toggles, so it's idempotent.
    [HttpPut("preferences")]
    public async Task<IActionResult> SetPreferences([FromBody] SetPreferencesRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var items = request.Preferences ?? [];
        if (items.Any(p => !NotificationTypePolicy.IsMutable((NotificationType)p.Type)))
        {
            throw new InvalidNotificationPreferenceException();
        }

        // Replace-the-set: drop the user's existing rows, then insert one per provided mutable type.
        var existing = await _dbContext.UserNotificationPreferences.Where(p => p.UserId == userId).ToListAsync(cancellationToken);
        _dbContext.UserNotificationPreferences.RemoveRange(existing);

        foreach (var item in items.DistinctBy(p => p.Type))
        {
            _dbContext.UserNotificationPreferences.Add(new UserNotificationPreference
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Type = (NotificationType)item.Type,
                EmailEnabled = item.EmailEnabled,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(await BuildPreferencesAsync(userId, cancellationToken));
    }

    private async Task<PreferencesResource> BuildPreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var stored = await _dbContext.UserNotificationPreferences
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.Type, p => p.EmailEnabled, cancellationToken);

        return new PreferencesResource
        {
            Preferences = NotificationTypePolicy.Mutable.Select(type => new PreferenceResource
            {
                Type = (int)type,
                TypeName = type.ToString(),
                EmailEnabled = stored.GetValueOrDefault(type, true),
            }).ToList(),
            Links = [new Link("self", Url.Action(nameof(GetPreferences))!, "GET")],
        };
    }

    private static NotificationResource BuildResource(Notification n, IReadOnlyDictionary<Guid, Guid?> parents) => new()
    {
        Id = n.Id,
        Type = n.Type.ToString(),
        Title = n.Title,
        Body = n.Body,
        EventCount = n.EventCount,
        DocumentId = n.DocumentId,
        DocumentParentId = n.DocumentId is { } d && parents.TryGetValue(d, out var parent) ? parent : null,
        CreatedAt = n.CreatedAt,
        ReadAt = n.ReadAt,
        IsRead = n.ReadAt is not null,

        // "Mark this one read" as a rel, so the bell menu stops composing /notifications/{id}/read (issue #416).
        // Present only while it is unread: the POST is idempotent, so this is not about preventing a bad call —
        // it is ADR 0543's "a missing rel is meaningful". An already-read notification offers nothing to do, and
        // a client that reads the rel rather than the flag draws the same conclusion the server already reached.
        //
        // `document` — the subject's own address, for a documentful notification (#443): this row was the last
        // id-bearing payload that handed a client an id with no address, which is what kept the desktop's
        // DocumentAddress composition alive. Task rows, reminder rows, followed rows and search hits already
        // carried theirs.
        Links = BuildLinks(n, parents),
    };

    private static List<Link> BuildLinks(Notification n, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        var links = new List<Link>();
        if (n.ReadAt is null)
        {
            links.Add(new Link("read", $"/api/notifications/{n.Id}/read", "POST"));
        }

        if (n.DocumentId is { } documentId)
        {
            links.Add(new Link("document", $"/api/documents/{documentId}", "GET"));

            // The subject's home folder — where "open" actually navigates to (#443). Absent at a root, where
            // opening the document itself as a folder is the client's correct fallback.
            if (parents.TryGetValue(documentId, out var parent) && parent is { } parentId)
            {
                links.Add(new Link("parent", $"/api/documents/{parentId}", "GET"));
            }
        }

        return links;
    }
}
