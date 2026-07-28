using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Comments;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// A Document's comment/chat thread — see ADR "Document comment thread". Append-only for
/// now (list + add, no edit/delete). Reading and posting both require CanSee (anyone who can see the
/// document can comment). One level of threading: a reply's ParentCommentId must be a top-level comment on
/// the same document. Authorization accepts either a ServiceAccount or a logged-in User caller.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/comments")]
[Authorize]
public class DocumentCommentsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly INotificationService _notifications;

    public DocumentCommentsController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        INotificationService notifications,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _notifications = notifications;
        _audit = audit;
    }

    private readonly IAuditRecorder _audit;

    // Plain mutable classes, not records — XmlSerializer (ADR "JSON/XML content negotiation").
    public class CommentResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public Guid? ParentCommentId { get; set; }

        public string Body { get; set; } = "";

        public string AuthorName { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }
    }

    public class CommentListResource : HypermediaResource
    {
        public List<CommentResource> Comments { get; set; } = [];
    }

    public class CreateCommentRequest
    {
        public string Body { get; set; } = "";

        public Guid? ParentCommentId { get; set; }
    }

    private record CommentRow(Guid Id, Guid? ParentCommentId, string Body, DateTimeOffset CreatedAt, string? AuthorName);

    // Cursor-based pagination (?cursor=&limit=), CreatedAt ascending / Id ascending — same shape as every
    // other list endpoint. Threaded rendering (grouping replies under parents) is the client's job; a reply
    // whose parent fell on an earlier page just renders ungrouped (small threads make this a non-issue).
    [HttpGet]
    public async Task<IActionResult> List(Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        // Serve a soft-deleted (recycle-bin) document's thread too (ADR "Recycle bin tab") — the detail pane's
        // chat shows it read-only; posting a comment (below) keeps the filter, so a deleted item's thread is
        // read-only.
        if (!await _dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.DocumentComments.Where(c => c.DocumentId == documentId);

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(c => c.CreatedAt > cursorCreatedAt || (c.CreatedAt == cursorCreatedAt && c.Id > cursorId));
        }

        var fetched = await query
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Take(pageSize + 1)
            .Select(c => new CommentRow(
                c.Id,
                c.ParentCommentId,
                c.Body,
                c.CreatedAt,
                c.CreatedByUserId != null
                    ? _dbContext.Users.Where(u => u.Id == c.CreatedByUserId).Select(u => u.Email).FirstOrDefault()
                    : _dbContext.ServiceAccounts.Where(s => s.Id == c.CreatedByServiceAccountId).Select(s => s.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { documentId, cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new CommentListResource
        {
            Comments = page.Select(ToResource).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Add(Guid documentId, [FromBody] CreateCommentRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId, d.CreatedByUserId, d.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new EmptyCommentException();
        }

        if (request.ParentCommentId is { } parentId)
        {
            // One level only: the parent must be a top-level comment on this same document.
            var parentIsTopLevel = await _dbContext.DocumentComments
                .AnyAsync(c => c.Id == parentId && c.DocumentId == documentId && c.ParentCommentId == null, cancellationToken);

            if (!parentIsTopLevel)
            {
                throw new InvalidParentCommentException();
            }
        }

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();

        var comment = new DocumentComment
        {
            Id = Guid.NewGuid(),
            TenantId = document.TenantId,
            DocumentId = documentId,
            ParentCommentId = request.ParentCommentId,
            Body = request.Body,
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.DocumentComments.Add(comment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var authorName = createdByUserId is { } uid
            ? await _dbContext.Users.Where(u => u.Id == uid).Select(u => u.Email).SingleAsync(cancellationToken)
            : await _dbContext.ServiceAccounts.Where(s => s.Id == createdByServiceAccountId).Select(s => s.Name).SingleAsync(cancellationToken);

        // Notify the person the comment lands on: a reply notifies the parent comment's author; a top-level
        // comment notifies the document's creator (the NotificationService skips self-notification).
        var recipientId = request.ParentCommentId is { } pid
            ? await _dbContext.DocumentComments.Where(c => c.Id == pid).Select(c => c.CreatedByUserId).SingleAsync(cancellationToken)
            : document.CreatedByUserId;
        if (recipientId is { } rid)
        {
            var verb = request.ParentCommentId is null ? "commented on" : "replied on";
            await _notifications.NotifyAsync(rid, NotificationType.CommentPosted, "New comment", $"{authorName} {verb} '{document.Name}'.", documentId, cancellationToken);
        }

        // Notify everyone following the document (ADR "Document subscriptions"), except the actor and the
        // recipient just notified above (so a follower isn't double-notified for one comment).
        await _notifications.NotifyDocumentSubscribersAsync(documentId, NotificationType.SubscribedActivity,
            "New comment", $"{authorName} commented on '{document.Name}'.",
            recipientId is { } r ? [r] : null, cancellationToken);

        await _audit.RecordAsync(AuditActions.CommentPosted, "Document", documentId, document.Name,
            request.ParentCommentId is null ? "Comment posted" : "Reply posted", cancellationToken: cancellationToken);

        var resource = ToResource(new CommentRow(comment.Id, comment.ParentCommentId, comment.Body, comment.CreatedAt, authorName));

        return StatusCode(StatusCodes.Status201Created, resource);
    }

    // No self link: a comment isn't individually addressable (no single-comment GET endpoint) — it's only
    // ever part of a document's thread.
    private static CommentResource ToResource(CommentRow row) => new()
    {
        Id = row.Id,
        ParentCommentId = row.ParentCommentId,
        Body = row.Body,
        AuthorName = row.AuthorName ?? "Unknown",
        CreatedAt = row.CreatedAt,
    };

    private async Task<bool> CanSeeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (await _effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken)).CanSee;
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee;
        }

        return false;
    }

    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity()
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (null, serviceAccountId);
        }

        return (_currentUserAccessor.UserId, null);
    }
}
