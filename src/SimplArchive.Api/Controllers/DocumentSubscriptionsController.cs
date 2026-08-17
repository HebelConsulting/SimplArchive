using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Subscribe to (follow) a document to be notified when it changes — a new confirmed version, a new
/// comment/reply, or the approval workflow reaching Released (ADR "Document subscriptions"). Per-User only (a
/// ServiceAccount has no in-app intray). Subscribing requires CanSee on the document; the subscription is the
/// caller's own, so reading its state / unsubscribing needs no further right.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/subscription")]
[Authorize]
public class DocumentSubscriptionsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public DocumentSubscriptionsController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentUserAccessor = currentUserAccessor;
    }

    public class SubscriptionResource : HypermediaResource
    {
        public bool Subscribed { get; set; }
    }

    // Is the calling user following this document?
    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid(); // a ServiceAccount has no in-app intray to notify
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        var subscribed = await _dbContext.DocumentSubscriptions
            .AnyAsync(s => s.DocumentId == documentId && s.UserId == userId, cancellationToken);
        return Ok(BuildResource(documentId, subscribed));
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is null)
        {
            return Forbid();
        }

        return await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken) ? NoContent() : NotFound();
    }

    // Subscribe (idempotent). Requires CanSee on the document — you can only follow what you can see.
    [HttpPut]
    public async Task<IActionResult> Subscribe(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var tenantId = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => (Guid?)d.TenantId)
            .SingleOrDefaultAsync(cancellationToken);
        if (tenantId is null)
        {
            return NotFound();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        var exists = await _dbContext.DocumentSubscriptions
            .AnyAsync(s => s.DocumentId == documentId && s.UserId == userId, cancellationToken);
        if (!exists)
        {
            _dbContext.DocumentSubscriptions.Add(new DocumentSubscription
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId.Value,
                UserId = userId,
                DocumentId = documentId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(BuildResource(documentId, true));
    }

    // Unsubscribe (idempotent) — always allowed for the caller's own subscription.
    [HttpDelete]
    public async Task<IActionResult> Unsubscribe(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var existing = await _dbContext.DocumentSubscriptions
            .Where(s => s.DocumentId == documentId && s.UserId == userId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _dbContext.DocumentSubscriptions.RemoveRange(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    private SubscriptionResource BuildResource(Guid documentId, bool subscribed) => new()
    {
        Subscribed = subscribed,
        Links =
        [
            new Link("self", Url.Action(nameof(Get), new { documentId })!, "GET"),
            subscribed
                ? new Link("unsubscribe", Url.Action(nameof(Get), new { documentId })!, "DELETE")
                : new Link("subscribe", Url.Action(nameof(Get), new { documentId })!, "PUT"),
        ],
    };
}
