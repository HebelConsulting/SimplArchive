using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Every external link a person has created, across all documents (ADR 0546, issue #385) — the view that lets
/// somebody audit what they have shared without hunting document by document.
///
/// A tenant admin may look at someone else's links via <c>?userId=</c>, or at a whole group's via
/// <c>?groupId=</c>. Shares the <c>api/external-links</c> route prefix with the anonymous redemption endpoint,
/// but is authenticated: the two actions differ by template (<c>/</c> versus <c>/{token}</c>), so they never
/// collide.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/external-links")]
[Authorize]
public class MyExternalLinksController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ICurrentTenantAccessor _tenant;
    private readonly TimeProvider _clock;

    public MyExternalLinksController(
        SimplArchiveDbContext dbContext,
        IUserSystemRightsResolver userSystemRights,
        ICurrentUserAccessor currentUser,
        ICurrentTenantAccessor tenant,
        TimeProvider clock)
    {
        _dbContext = dbContext;
        _userSystemRights = userSystemRights;
        _currentUser = currentUser;
        _tenant = tenant;
        _clock = clock;
    }

    private const int ExtendableWithinDays = 30;

    public class MyExternalLinkResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public Guid DocumentId { get; set; }

        // Which document this shares — the whole point of a cross-document view is knowing WHAT was shared.
        public string DocumentName { get; set; } = string.Empty;

        // The document's folder, so the client's "Go to" can open that folder and select the row inside it. Null
        // only for a repository root, which cannot be shared anyway (a folder is not shareable).
        public Guid? ParentId { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public int? MaxAccesses { get; set; }

        public int AccessCount { get; set; }

        public string CreatedByName { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public bool CanExtend { get; set; }

        public string Etag { get; set; } = string.Empty;
    }

    public class MyExternalLinkListResource : HypermediaResource
    {
        public List<MyExternalLinkResource> ExternalLinks { get; set; } = [];

        // Client hint: whether the caller may use the userId/groupId filters at all.
        public bool CanViewOthers { get; set; }
    }

    /// <summary>
    /// The caller's live links; a tenant admin may pass <c>userId</c> or <c>groupId</c> to view another's.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? userId, [FromQuery] Guid? groupId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        // Only a person shares a document (ADR 0546), so only a person has links to list. A service account
        // reaching this endpoint has nothing to show rather than an empty list to puzzle over.
        if (_currentUser.UserId is not { } callerId)
        {
            return Forbid();
        }

        var isAdmin = (await _userSystemRights.GetEffectiveSystemRightsAsync(callerId, cancellationToken)).IsTenantAdmin;

        // Looking at anyone but yourself is an administrative act. Without this, the filters would let any user
        // enumerate what colleagues have shared.
        if ((userId is not null && userId != callerId) || groupId is not null)
        {
            if (!isAdmin)
            {
                return Forbid();
            }
        }

        var query = _dbContext.ExternalLinks.Where(l => l.RevokedAt == null && l.ExpiresAt > now);

        if (groupId is { } group)
        {
            // "Links created by anyone CURRENTLY in this group" — membership is evaluated here, at query time,
            // rather than stored on the link (ADR 0546). Someone who leaves the group drops out of this view while
            // their links keep working, so the picker answers "who can I see now", not "who owned this then".
            var memberIds = await _dbContext.GroupMemberships
                .Where(m => m.GroupId == group)
                .Select(m => m.UserId)
                .ToListAsync(cancellationToken);

            query = query.Where(l => memberIds.Contains(l.CreatedByUserId));
        }
        else
        {
            var subject = userId ?? callerId;
            query = query.Where(l => l.CreatedByUserId == subject);
        }

        return Ok(await BuildAsync(query, now, isAdmin, cancellationToken));
    }

    private async Task<MyExternalLinkListResource> BuildAsync(
        IQueryable<Domain.Documents.ExternalLink> query, DateTimeOffset now, bool canViewOthers, CancellationToken cancellationToken)
    {
        // As on the per-document list: the reveal rel appears only where the tenant opted in (issue #412), and
        // its ABSENCE is how the client knows not to offer the affordance rather than offering one that 403s.
        var showsUrl = await _dbContext.Tenants
            .Where(t => t.Id == _tenant.TenantId!.Value)
            .Select(t => t.ShowExternalLinkUrl)
            .SingleAsync(cancellationToken);

        var rows = await query
            .OrderBy(l => l.ExpiresAt).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.DocumentId,
                l.ExpiresAt,
                l.MaxAccesses,
                l.AccessCount,
                l.CreatedAt,
                l.ConcurrencyToken,
                DocumentName = _dbContext.Documents.Where(d => d.Id == l.DocumentId).Select(d => d.Name).FirstOrDefault(),
                ParentId = _dbContext.Documents.Where(d => d.Id == l.DocumentId).Select(d => d.ParentId).FirstOrDefault(),
                CreatedByName = _dbContext.Users.Where(u => u.Id == l.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return new MyExternalLinkListResource
        {
            ExternalLinks = rows.Select(l => new MyExternalLinkResource
            {
                Id = l.Id,
                DocumentId = l.DocumentId,
                DocumentName = l.DocumentName ?? "",
                ParentId = l.ParentId,
                ExpiresAt = l.ExpiresAt,
                MaxAccesses = l.MaxAccesses,
                AccessCount = l.AccessCount,
                CreatedByName = l.CreatedByName ?? "Unknown",
                CreatedAt = l.CreatedAt,
                CanExtend = l.ExpiresAt <= now.AddDays(ExtendableWithinDays),
                Etag = l.ConcurrencyToken.ToString(),
                // Revoke and availability live under the document, so the client follows these rather than
                // composing them (ADR 0543). `document`/`parent` carry the addresses the "Go to" action
                // follows — since the #443 endgame the desktop navigates by fetching the advertised parent,
                // so a row naming a document without its address would leave an id it can only compose from.
                Links =
                [
                    new Link("revoke", $"/api/documents/{l.DocumentId}/external-links/{l.Id}", "DELETE"),
                    new Link("availability", $"/api/documents/{l.DocumentId}/external-links/{l.Id}/availability", "PUT"),
                    new Link("document", $"/api/documents/{l.DocumentId}", "GET"),
                    .. l.ParentId is { } linkParentId
                        ? new[] { new Link("parent", $"/api/documents/{linkParentId}", "GET") }
                        : [],
                    .. showsUrl
                        ? new[] { new Link("reveal-url", $"/api/documents/{l.DocumentId}/external-links/{l.Id}/url", "GET") }
                        : [],
                ],
            }).ToList(),
            CanViewOthers = canViewOthers,
            Links = [new Link("self", "/api/external-links", "GET")],
        };
    }

    // Standing convention: every GET action gets a companion HEAD. (The anonymous redemption GET is the one
    // deliberate exception — see ExternalLinksController.)
    [HttpHead]
    public IActionResult Head() => _currentUser.UserId is null ? Forbid() : NoContent();
}
