using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The documents the caller follows (ADR "My work dashboard") — their subscriptions across all documents, for
/// the personal dashboard's "Following" section. User-only (a ServiceAccount has no subscriptions). A
/// followed document that's been soft-deleted is excluded (the Documents join is soft-delete filtered).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/subscriptions")]
[Authorize]
public class SubscriptionsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public SubscriptionsController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
    }

    public class FollowedResource : HypermediaResource
    {
        public Guid DocumentId { get; set; }
        public Guid? ParentId { get; set; }
        public string DocumentName { get; set; } = "";
        public DateTimeOffset FollowedAt { get; set; }
    }

    public class FollowedListResource : HypermediaResource
    {
        public List<FollowedResource> Followed { get; set; } = [];
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var followed = await _dbContext.DocumentSubscriptions
            .Where(s => s.UserId == userId)
            .Join(_dbContext.Documents, s => s.DocumentId, d => d.Id, (s, d) => new FollowedResource
            {
                DocumentId = d.Id,
                ParentId = d.ParentId,
                DocumentName = d.Name,
                FollowedAt = s.CreatedAt,
                Links = new List<Link> { new("document", $"/api/documents/{d.Id}", "GET") },
            })
            .OrderByDescending(f => f.FollowedAt)
            .ToListAsync(cancellationToken);

        return Ok(new FollowedListResource { Followed = followed, Links = [new Link("self", "/api/subscriptions", "GET")] });
    }

    [HttpHead]
    public IActionResult Head() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();
}
