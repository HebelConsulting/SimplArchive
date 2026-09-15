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
    private readonly IDocumentIndexQueue _queue;
    private readonly Concurrency.DocumentVerbs _documents;
    private readonly IAuditRecorder _audit;
    private readonly Documents.DocumentAccessService _access;
    private readonly Documents.TagSetWriter _tags;

    public DocumentTagsController(
        SimplArchiveDbContext dbContext,
        IDocumentIndexQueue queue,
        IAuditRecorder audit,
        Documents.DocumentAccessService access,
        Documents.TagSetWriter tags,
        Concurrency.DocumentVerbs documents)
    {
        _dbContext = dbContext;
        _queue = queue;
        _audit = audit;
        _access = access;
        _tags = tags;
        _documents = documents;
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
        // The ENTITY rather than a projection: TagSetWriter takes the document so the combined
        // PUT .../detail (ADR 0794) can hand it the one it already loaded. Tags write no column on it.
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanEditIndexData)
        {
            return Forbid();
        }

        // Normalization, the tag catalog and the row rewrite belong to TagSetWriter, which ADR 0794's
        // combined PUT .../detail calls too. What stays here is the envelope.
        // Through the document's contract (#1227). Tags are CHILD ROWS on a tracked parent, the shape
        // SetIndexData already solved: EF checks a token only on rows it is actually updating, so without
        // touchEntity (default) marking the document modified, a precondition here would be enforced and
        // inert — the defect #1167 found in this controller's neighbour.
        //
        // The re-index rides in afterCommit: fired before it, the index is told a tag set the database may
        // still roll back.
        List<string> normalized = null!;
        await _documents.MutateAsync(
            Request,
            document,
            apply: async () => normalized = await _tags.ApplyAsync(document, request.Tags, cancellationToken),
            afterCommit: () => _queue.EnqueueAsync(documentId, cancellationToken),
            cancellationToken: cancellationToken);
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

    private Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken) =>
        _access.GetCallerRightsAsync(documentId, cancellationToken);
}
