using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Tags;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// A document's free-form tags/labels (ADR "Document tags") — cross-cutting, searchable categorization,
/// distinct from a mask's structured index fields. Reading requires <c>CanSee</c>; replacing the tag set
/// requires <c>CanEditIndexData</c> (tags are metadata). Tags are lightweight labels, so — like comments —
/// they aren't blocked by a legal hold / check-out. Accepts either a ServiceAccount or a User caller.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/tags")]
[Authorize]
public class DocumentTagsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IDocumentIndexQueue _queue;
    private readonly IAuditRecorder _audit;

    public DocumentTagsController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IDocumentIndexQueue queue,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _queue = queue;
        _audit = audit;
    }

    public class TagsResource : HypermediaResource
    {
        public List<string> Tags { get; set; } = [];
    }

    public class SetTagsRequest
    {
        public List<string> Tags { get; set; } = [];
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => new { d.TenantId }).SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        return Ok(BuildResource(documentId, await LoadTagsAsync(documentId, cancellationToken)));
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanSee ? NoContent() : Forbid();
    }

    // Replaces the document's whole tag set (PUT-replaces-all, like index-data). Normalizes to trimmed
    // lowercase, dedupes, drops blanks/over-length, re-indexes, and audits.
    [HttpPut]
    public async Task<IActionResult> Set(Guid documentId, [FromBody] SetTagsRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => new { d.TenantId, d.Name }).SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanEditIndexData)
        {
            return Forbid();
        }

        var normalized = (request.Tags ?? [])
            .Select(t => (t ?? "").Trim().ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 100)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        // Tag-catalog enforcement (ADR "Tag controlled vocabulary"): when the tenant restricts tagging, every tag
        // must already be in the active catalog; otherwise a newly-typed tag is added to the catalog so it curates
        // itself going forward.
        var restrict = await _dbContext.Tenants.Where(t => t.Id == document.TenantId).Select(t => t.RestrictTagsToCatalog).SingleAsync(cancellationToken);
        var activeCatalog = (await _dbContext.TagDefinitions.Where(t => t.RetiredAt == null).Select(t => t.Name).ToListAsync(cancellationToken)).ToHashSet();
        if (restrict)
        {
            if (normalized.FirstOrDefault(t => !activeCatalog.Contains(t)) is { } unknown)
            {
                throw new UnknownTagException(unknown);
            }
        }
        else
        {
            foreach (var tag in normalized.Where(t => !activeCatalog.Contains(t)))
            {
                _dbContext.TagDefinitions.Add(new TagDefinition { Id = Guid.NewGuid(), TenantId = document.TenantId, Name = tag, CreatedAt = DateTimeOffset.UtcNow });
            }
        }

        var existing = await _dbContext.DocumentTags.Where(t => t.DocumentId == documentId).ToListAsync(cancellationToken);
        _dbContext.DocumentTags.RemoveRange(existing);
        var now = DateTimeOffset.UtcNow;
        foreach (var tag in normalized)
        {
            _dbContext.DocumentTags.Add(new DocumentTag { Id = Guid.NewGuid(), TenantId = document.TenantId, DocumentId = documentId, Tag = tag, CreatedAt = now });
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _queue.EnqueueAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentTagsUpdated, "Document", documentId, document.Name,
            normalized.Count == 0 ? "Tags cleared" : $"Tags: {string.Join(", ", normalized)}", cancellationToken: cancellationToken);

        return Ok(BuildResource(documentId, normalized));
    }

    private async Task<List<string>> LoadTagsAsync(Guid documentId, CancellationToken cancellationToken) =>
        await _dbContext.DocumentTags.Where(t => t.DocumentId == documentId).OrderBy(t => t.Tag).Select(t => t.Tag).ToListAsync(cancellationToken);

    private TagsResource BuildResource(Guid documentId, List<string> tags) => new()
    {
        Tags = tags,
        Links = [new Link("self", $"/api/documents/{documentId}/tags", "GET")],
    };

    private async Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken);
        }

        return new EffectiveRights(false, false, false, false, false, false, false, false, false);
    }
}
