using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Chat;
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
/// document can comment). One level of threading: a reply's ParentMessageId must be a top-level comment on
/// the same document. Authorization accepts either a ServiceAccount or a logged-in User caller.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/chat")]
[Authorize]
public class DocumentChatController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly INotificationService _notifications;

    public DocumentChatController(
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
    public class ChatMessageResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public Guid? ParentMessageId { get; set; }

        public string Body { get; set; } = "";

        // What produced this entry (ADR 0545): 0 UserPost · 1 VersionFiled · 2 VersionActivated, the Api's
        // enum-as-int convention. A client renders a localized sentence for anything but 0, so Body is empty
        // there and must not be displayed — and for VersionFiled it picks the sentence by VersionNumber.
        public int Kind { get; set; }

        // For a version entry: the version's number and its check-in comment, read live from the referenced
        // version rather than copied at post time — so editing a comment updates the feed instead of leaving a
        // stale copy. Null on a UserPost.
        public int? VersionNumber { get; set; }

        public string? VersionComment { get; set; }

        // 0 UserComment · 1 SearchablePdfGenerated. A generated comment carries no text: the client renders the
        // localized sentence for the kind.
        public int? VersionCommentKind { get; set; }

        public string AuthorName { get; set; } = "";

        // Null when a ServiceAccount authored the message. A client uses the "author-card" rel rather than
        // composing a URL from this, but the id identifies the author (e.g. to group or highlight own posts).
        public Guid? AuthorUserId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        // Who this message addresses (issue #383), resolved for rendering. The BODY carries only ids, as
        // "@[{userId}]" tokens — a display name is neither unique nor stable, so storing one would break on a
        // rename. The client substitutes each token with the name from here, which is why the name is sent
        // alongside rather than being looked up per token.
        public List<ChatMentionResource> Mentions { get; set; } = [];
    }

    public class ChatMentionResource
    {
        public Guid UserId { get; set; }

        public string DisplayName { get; set; } = "";
    }

    public class ChatMessageListResource : HypermediaResource
    {
        public List<ChatMessageResource> Messages { get; set; } = [];
    }

    // A user who may be @-mentioned on this document.
    public class MentionableUserResource
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = "";
    }

    public class MentionableUserListResource : HypermediaResource
    {
        public List<MentionableUserResource> Users { get; set; } = [];
    }

    public class CreateChatMessageRequest
    {
        public string Body { get; set; } = "";

        public Guid? ParentMessageId { get; set; }
    }

    // AuthorUserId / AuthorServiceAccountId: exactly one is set, mirroring the entity. The id is what lets a
    // client fetch the author's card (ADR 0544); the name alone was not enough to identify anyone.
    private record ChatMessageRow(
        Guid Id, Guid? ParentMessageId, string Body, DateTimeOffset CreatedAt, string? AuthorName,
        Guid? AuthorUserId, Guid? AuthorServiceAccountId,
        ChatMessageKind Kind, int? VersionNumber, string? VersionComment, VersionCommentKind? VersionCommentKind);

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

        var query = _dbContext.ChatMessages.Where(c => c.DocumentId == documentId);

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(c => c.CreatedAt > cursorCreatedAt || (c.CreatedAt == cursorCreatedAt && c.Id > cursorId));
        }

        var fetched = await query
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Take(pageSize + 1)
            .Select(c => new ChatMessageRow(
                c.Id,
                c.ParentMessageId,
                c.Body,
                c.CreatedAt,
                // DisplayName, not Email: the thread shows a person's NAME, and the email now lives on the card
                // behind it (ADR 0544). This previously rendered the raw email address as the author label.
                c.CreatedByUserId != null
                    ? _dbContext.Users.Where(u => u.Id == c.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault()
                    : _dbContext.ServiceAccounts.Where(s => s.Id == c.CreatedByServiceAccountId).Select(s => s.Name).FirstOrDefault(),
                c.CreatedByUserId,
                c.CreatedByServiceAccountId,
                c.Kind,
                // Read from the referenced version, not stored on the message (ADR 0545) — a later edit to the
                // check-in comment shows through instead of leaving a stale copy in the feed.
                _dbContext.DocumentVersions.Where(v => v.Id == c.DocumentVersionId).Select(v => v.VersionNumber).FirstOrDefault(),
                _dbContext.DocumentVersions.Where(v => v.Id == c.DocumentVersionId).Select(v => v.Comment).FirstOrDefault(),
                _dbContext.DocumentVersions.Where(v => v.Id == c.DocumentVersionId).Select(v => (VersionCommentKind?)v.CommentKind).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        // Mentions for the whole page in one round trip, then joined in memory — a per-message subquery would
        // multiply the thread's query count by its length for a feature most messages don't use.
        var messageIds = page.Select(p => p.Id).ToList();
        var mentions = (await _dbContext.ChatMessageMentions
                .Where(m => messageIds.Contains(m.ChatMessageId))
                .Select(m => new
                {
                    m.ChatMessageId,
                    m.UserId,
                    DisplayName = _dbContext.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault(),
                })
                .ToListAsync(cancellationToken))
            .GroupBy(m => m.ChatMessageId)
            .ToDictionary(g => g.Key, g => g.Select(m => new ChatMentionResource
            {
                UserId = m.UserId,
                // A mention whose user no longer resolves still renders as a readable sentence rather than a raw
                // id — the record that somebody was addressed outlives the account.
                DisplayName = m.DisplayName ?? UnknownMentionName,
            }).ToList());

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { documentId, cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        // The picker's endpoint is advertised here rather than composed by the client (ADR 0543). It is on the
        // thread because that is where mentioning happens.
        links.Add(new Link("mentionable-users", Url.Action(nameof(MentionableUsers), new { documentId })!, "GET"));

        return Ok(new ChatMessageListResource
        {
            Messages = page.Select(row => ToResource(row, mentions.GetValueOrDefault(row.Id) ?? [])).ToList(),
            Links = links,
        });
    }

    // The users this caller may address on this document. ACL-filtered, NOT a tenant-wide directory: mentioning
    // somebody auto-subscribes them and sends a notification carrying the document's NAME, so offering a user who
    // cannot see the document would leak it. (The pre-existing grantable-principals endpoint looks similar but is
    // neither — it lists every active user and is gated on CanManagePermissions, which an ordinary commenter has
    // no reason to hold.)
    [HttpGet("mentionable-users")]
    public async Task<IActionResult> MentionableUsers(Guid documentId, [FromQuery] string? q, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var users = await MentionCandidatesAsync(q, cancellationToken);

        var visible = new List<MentionableUserResource>();
        foreach (var user in users)
        {
            // One rights walk per candidate. Bounded by narrowing on the name FIRST — the picker filters as the
            // caller types, so in practice this runs over a handful of names, not the tenant.
            if ((await _effectiveRightsCalculator.GetEffectiveRightsAsync(user.Id, documentId, cancellationToken)).CanSee)
            {
                visible.Add(new MentionableUserResource { Id = user.Id, DisplayName = user.DisplayName });
            }

            if (visible.Count == MentionPickerSize)
            {
                break;
            }
        }

        return Ok(new MentionableUserListResource
        {
            Users = visible,
            Links = [new Link("self", Url.Action(nameof(MentionableUsers), new { documentId, q })!, "GET")],
        });
    }

    [HttpHead("mentionable-users")]
    public async Task<IActionResult> HeadMentionableUsers(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return await CanSeeAsync(documentId, cancellationToken) ? NoContent() : Forbid();
    }

    // How many names the picker shows, and how many candidates are rights-checked to fill it. The candidate cap
    // is the higher of the two because the name filter runs before the rights filter: without headroom, a caller
    // whose first matches are all invisible to them would see an empty picker rather than the reachable ones.
    private const int MentionPickerSize = 20;
    private const int MentionCandidateCap = 100;

    private const string UnknownMentionName = "Unknown user";

    private async Task<List<(Guid Id, string DisplayName)>> MentionCandidatesAsync(string? q, CancellationToken cancellationToken)
    {
        var query = _dbContext.Users.Where(u => u.IsActive);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(u => EF.Functions.Like(u.DisplayName.ToUpper(), $"%{term.ToUpper()}%"));
        }

        return (await query
                .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
                .Take(MentionCandidateCap)
                .Select(u => new { u.Id, u.DisplayName })
                .ToListAsync(cancellationToken))
            .Select(u => (u.Id, u.DisplayName))
            .ToList();
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
    public async Task<IActionResult> Add(Guid documentId, [FromBody] CreateChatMessageRequest request, CancellationToken cancellationToken)
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
            throw new EmptyChatMessageException();
        }

        if (request.ParentMessageId is { } parentId)
        {
            // One level only: the parent must be a top-level comment on this same document.
            var parentIsTopLevel = await _dbContext.ChatMessages
                .AnyAsync(c => c.Id == parentId && c.DocumentId == documentId && c.ParentMessageId == null, cancellationToken);

            if (!parentIsTopLevel)
            {
                throw new InvalidParentChatMessageException();
            }
        }

        // Mentions are validated BEFORE the message is written: a token naming somebody who cannot see this
        // document would subscribe them to it and send them a notification carrying its NAME. Rejecting keeps
        // that impossible rather than merely unlikely — the picker only ever offers visible users, so a caller
        // reaches this only by hand-crafting a request, or by losing a race with an ACL change.
        var mentionedUserIds = ChatMentions.Parse(request.Body);
        foreach (var mentionedId in mentionedUserIds)
        {
            var mentionedIsActive = await _dbContext.Users.AnyAsync(u => u.Id == mentionedId && u.IsActive, cancellationToken);
            if (!mentionedIsActive
                || !(await _effectiveRightsCalculator.GetEffectiveRightsAsync(mentionedId, documentId, cancellationToken)).CanSee)
            {
                throw new InvalidChatMentionException(mentionedId);
            }
        }

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();

        var comment = new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = document.TenantId,
            DocumentId = documentId,
            ParentMessageId = request.ParentMessageId,
            Body = request.Body,
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.ChatMessages.Add(comment);

        foreach (var mentionedId in mentionedUserIds)
        {
            _dbContext.ChatMessageMentions.Add(new ChatMessageMention
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                ChatMessageId = comment.Id,
                UserId = mentionedId,
                CreatedAt = comment.CreatedAt,
            });
        }

        // Being addressed subscribes you to the document, so the answers to what you were asked reach you too.
        // Deliberately re-subscribes somebody who unsubscribed earlier (interviewed): a mention is treated as a
        // fresh request for attention rather than something a past opt-out silences. Unsubscribing again is one
        // click on the document, which is what keeps that bearable.
        var alreadySubscribed = await _dbContext.DocumentSubscriptions
            .Where(s => s.DocumentId == documentId && mentionedUserIds.Contains(s.UserId))
            .Select(s => s.UserId)
            .ToListAsync(cancellationToken);

        foreach (var mentionedId in mentionedUserIds.Where(id => !alreadySubscribed.Contains(id)))
        {
            _dbContext.DocumentSubscriptions.Add(new DocumentSubscription
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                UserId = mentionedId,
                DocumentId = documentId,
                CreatedAt = comment.CreatedAt,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // DisplayName, matching the list projection — the thread shows a person's NAME, and their email now lives
        // on the card behind it (ADR 0544). Both paths previously rendered the raw email address as the label.
        var authorName = createdByUserId is { } uid
            ? await _dbContext.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).SingleAsync(cancellationToken)
            : await _dbContext.ServiceAccounts.Where(s => s.Id == createdByServiceAccountId).Select(s => s.Name).SingleAsync(cancellationToken);

        // Notify the person the comment lands on: a reply notifies the parent comment's author; a top-level
        // comment notifies the document's creator (the NotificationService skips self-notification).
        var recipientId = request.ParentMessageId is { } pid
            ? await _dbContext.ChatMessages.Where(c => c.Id == pid).Select(c => c.CreatedByUserId).SingleAsync(cancellationToken)
            : document.CreatedByUserId;

        // Being named personally comes first, and is its own notification type so it does NOT fold into the
        // "3 new comments" digest that ChatMessagePosted coalesces into (ADR 0434) — a digest is how a direct
        // request gets missed.
        foreach (var mentionedId in mentionedUserIds)
        {
            await _notifications.NotifyAsync(mentionedId, NotificationType.ChatMentioned, "You were mentioned",
                $"{authorName} mentioned you on '{document.Name}'.", documentId, cancellationToken);
        }

        // Everyone below has either been told already or is being told something weaker about the same message.
        var alreadyTold = mentionedUserIds.ToList();

        if (recipientId is { } rid && !alreadyTold.Contains(rid))
        {
            var verb = request.ParentMessageId is null ? "commented on" : "replied on";
            await _notifications.NotifyAsync(rid, NotificationType.ChatMessagePosted, "New comment", $"{authorName} {verb} '{document.Name}'.", documentId, cancellationToken);
            alreadyTold.Add(rid);
        }

        // Notify everyone following the document (ADR "Document subscriptions"), except the actor and anyone
        // notified above — including the users just auto-subscribed BY this message, who would otherwise be told
        // twice about the message that subscribed them.
        await _notifications.NotifyDocumentSubscribersAsync(documentId, NotificationType.SubscribedActivity,
            "New comment", $"{authorName} commented on '{document.Name}'.",
            alreadyTold.Count > 0 ? alreadyTold : null, cancellationToken);

        await _audit.RecordAsync(AuditActions.ChatMessagePosted, "Document", documentId, document.Name,
            request.ParentMessageId is null ? "Comment posted" : "Reply posted", cancellationToken: cancellationToken);

        // A client can only ever create a UserPost; the system kinds are written by ChatSystemEntryRecorder.
        var mentionResources = await _dbContext.Users
            .Where(u => mentionedUserIds.Contains(u.Id))
            .Select(u => new ChatMentionResource { UserId = u.Id, DisplayName = u.DisplayName })
            .ToListAsync(cancellationToken);

        var resource = ToResource(new ChatMessageRow(comment.Id, comment.ParentMessageId, comment.Body, comment.CreatedAt, authorName,
            comment.CreatedByUserId, comment.CreatedByServiceAccountId,
            ChatMessageKind.UserPost, VersionNumber: null, VersionComment: null, VersionCommentKind: null), mentionResources);

        return StatusCode(StatusCodes.Status201Created, resource);
    }

    // No self link: a comment isn't individually addressable (no single-comment GET endpoint) — it's only
    // ever part of a document's thread.
    private static ChatMessageResource ToResource(ChatMessageRow row, List<ChatMentionResource> mentions) => new()
    {
        Mentions = mentions,
        Id = row.Id,
        ParentMessageId = row.ParentMessageId,
        Body = row.Body,
        AuthorName = row.AuthorName ?? "Unknown",
        AuthorUserId = row.AuthorUserId,
        CreatedAt = row.CreatedAt,
        Kind = (int)row.Kind,
        VersionNumber = row.VersionNumber,
        VersionComment = row.VersionComment,
        VersionCommentKind = (int?)row.VersionCommentKind,
        // The author's card, as a REL rather than a URL the client rebuilds (ADR 0543). Present only for a human
        // author: a ServiceAccount is an automation with no card to open, and its absence is how a client knows
        // to render the name as plain text (ADR 0544).
        Links = row.AuthorUserId is { } authorId
            ? [new Link("author-card", $"/api/users/{authorId}/card", "GET")]
            : [],
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
