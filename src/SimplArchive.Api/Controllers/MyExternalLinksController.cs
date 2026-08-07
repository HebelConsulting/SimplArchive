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
    private readonly ICurrentServiceAccountAccessor _currentServiceAccount;
    private readonly TimeProvider _clock;

    public MyExternalLinksController(
        SimplArchiveDbContext dbContext,
        IUserSystemRightsResolver userSystemRights,
        ICurrentUserAccessor currentUser,
        ICurrentServiceAccountAccessor currentServiceAccount,
        TimeProvider clock)
    {
        _dbContext = dbContext;
        _userSystemRights = userSystemRights;
        _currentUser = currentUser;
        _currentServiceAccount = currentServiceAccount;
        _clock = clock;
    }

    private const int ExtendableWithinDays = 30;

    public class MyExternalLinkResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public Guid DocumentId { get; set; }

        // Which document this shares — the whole point of a cross-document view is knowing WHAT was shared.
        public string DocumentName { get; set; } = "";

        public DateTimeOffset ExpiresAt { get; set; }

        public int? MaxAccesses { get; set; }

        public int AccessCount { get; set; }

        public string CreatedByName { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }

        public bool CanExtend { get; set; }

        public string Etag { get; set; } = "";
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
        [FromQuery] Guid? userId, [FromQuery] Guid? groupId, [FromQuery] Guid? serviceAccountId,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        // A ServiceAccount can hold CanCreateExternalLink, so it can create links — and must therefore be able to
        // see its own. Without this branch automation could share documents that never appeared in any
        // cross-document view, which is precisely the blind spot this view exists to remove.
        if (_currentServiceAccount.ServiceAccountId is { } callerServiceAccountId)
        {
            var own = _dbContext.ExternalLinks.Where(l =>
                l.RevokedAt == null && l.ExpiresAt > now && l.CreatedByServiceAccountId == callerServiceAccountId);
            return Ok(await BuildAsync(own, now, canViewOthers: false, cancellationToken));
        }

        if (_currentUser.UserId is not { } callerId)
        {
            return Forbid();
        }

        var isAdmin = (await _userSystemRights.GetEffectiveSystemRightsAsync(callerId, cancellationToken)).IsTenantAdmin;

        // Looking at anyone but yourself is an administrative act. Without this, the filters would let any user
        // enumerate what colleagues have shared.
        if ((userId is not null && userId != callerId) || groupId is not null || serviceAccountId is not null)
        {
            if (!isAdmin)
            {
                return Forbid();
            }
        }

        var query = _dbContext.ExternalLinks.Where(l => l.RevokedAt == null && l.ExpiresAt > now);

        if (serviceAccountId is { } account)
        {
            // Links created by automation. Invisible to the user/group filters by construction, so an admin
            // auditing "everything shared out of this tenant" needs this third lens.
            query = query.Where(l => l.CreatedByServiceAccountId == account);
        }
        else if (groupId is { } group)
        {
            // "Links created by anyone CURRENTLY in this group" — membership is evaluated here, at query time,
            // rather than stored on the link (ADR 0546). Someone who leaves the group drops out of this view while
            // their links keep working, so the picker answers "who can I see now", not "who owned this then".
            var memberIds = await _dbContext.GroupMemberships
                .Where(m => m.GroupId == group)
                .Select(m => m.UserId)
                .ToListAsync(cancellationToken);

            query = query.Where(l => l.CreatedByUserId != null && memberIds.Contains(l.CreatedByUserId.Value));
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
                CreatedByName = l.CreatedByUserId != null
                    ? _dbContext.Users.Where(u => u.Id == l.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault()
                    : _dbContext.ServiceAccounts.Where(a => a.Id == l.CreatedByServiceAccountId).Select(a => a.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return new MyExternalLinkListResource
        {
            ExternalLinks = rows.Select(l => new MyExternalLinkResource
            {
                Id = l.Id,
                DocumentId = l.DocumentId,
                DocumentName = l.DocumentName ?? "",
                ExpiresAt = l.ExpiresAt,
                MaxAccesses = l.MaxAccesses,
                AccessCount = l.AccessCount,
                CreatedByName = l.CreatedByName ?? "Unknown",
                CreatedAt = l.CreatedAt,
                CanExtend = l.ExpiresAt <= now.AddDays(ExtendableWithinDays),
                Etag = l.ConcurrencyToken.ToString(),
                // Revoke and extend live under the document, so the client follows these rather than composing
                // them (ADR 0543).
                Links =
                [
                    new Link("revoke", $"/api/documents/{l.DocumentId}/external-links/{l.Id}", "DELETE"),
                    new Link("extend", $"/api/documents/{l.DocumentId}/external-links/{l.Id}/expiry", "PUT"),
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
