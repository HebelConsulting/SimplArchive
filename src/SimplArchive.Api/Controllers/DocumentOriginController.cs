using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Ocr;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The document origin key (ADRs 0349/0520) — the (source system, source record) a document was imported
/// from, plus the by-origin lookup a re-import resolves against. Split out of DocumentsController (#466);
/// the routes are unchanged, and the rels that reach them are the compatibility surface (ADR 0543).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class DocumentOriginController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly Documents.DocumentAccessService _access;
    private readonly IAuditRecorder _audit;

    public DocumentOriginController(
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _access = access;
        _audit = audit;
    }

    // ── Origin key: generic external-system correlation (ADR 0349/0520) ────────────────────────────────────
    // Records the (source system, source record) a document was imported from, so a re-import can skip/update
    // instead of duplicating. Generic — not tied to any specific source system; reusable by any external import.

    public class SetOriginRequest
    {
        public Guid OriginTenantId { get; set; }
        public Guid OriginDocumentId { get; set; }
    }

    public class OriginResource : HypermediaResource
    {
        [System.Xml.Serialization.XmlElement(IsNullable = true)] public Guid? OriginTenantId { get; set; }
        [System.Xml.Serialization.XmlElement(IsNullable = true)] public Guid? OriginDocumentId { get; set; }
    }

    private OriginResource BuildOriginResource(Guid documentId, Guid? tenantId, Guid? documentIdOrigin) => new()
    {
        OriginTenantId = tenantId,
        OriginDocumentId = documentIdOrigin,
        Links = [new Link("self", $"/api/documents/{documentId}/origin", "GET")],
    };

    // Set/replace the document's origin key. Gated on CanImport, If-Match like any mutation.
    [HttpPut("origin")]
    public async Task<IActionResult> SetOrigin(Guid documentId, [FromBody] SetOriginRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        document.OriginTenantId = request.OriginTenantId;
        document.OriginDocumentId = request.OriginDocumentId;
        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForDocument();
        }

        await _audit.RecordAsync(AuditActions.DocumentOriginSet, "Document", documentId, document.Name,
            $"Origin set to {request.OriginTenantId}/{request.OriginDocumentId}", cancellationToken: cancellationToken);
        SetETag(document.ConcurrencyToken);
        return Ok(BuildOriginResource(documentId, document.OriginTenantId, document.OriginDocumentId));
    }

    [HttpGet("origin")]
    public async Task<IActionResult> GetOrigin(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.Where(d => d.Id == documentId)
            .Select(d => new { d.OriginTenantId, d.OriginDocumentId, d.ConcurrencyToken }).SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        SetETag(document.ConcurrencyToken);
        return Ok(BuildOriginResource(documentId, document.OriginTenantId, document.OriginDocumentId));
    }

    [HttpHead("origin")]
    public async Task<IActionResult> HeadOrigin(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.Where(d => d.Id == documentId)
            .Select(d => new { d.ConcurrencyToken }).SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        SetETag(document.ConcurrencyToken);
        return NoContent();
    }

    // Clear the document's origin key. Gated on CanImport, If-Match.
    [HttpDelete("origin")]
    public async Task<IActionResult> ClearOrigin(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        document.OriginTenantId = null;
        document.OriginDocumentId = null;
        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForDocument();
        }

        await _audit.RecordAsync(AuditActions.DocumentOriginCleared, "Document", documentId, document.Name, "Origin cleared", cancellationToken: cancellationToken);
        SetETag(document.ConcurrencyToken);
        return NoContent();
    }

    // Resolve the single document for an origin key, so an importer can skip/update instead of duplicating
    // (ADR 0520). Absolute route — escapes the controller's {documentId:guid} prefix. Gated on CanImport;
    // tenant-scoped by the query filter, and unique per (TenantId, OriginTenantId, OriginDocumentId).
    [HttpGet("/api/documents/by-origin/{originTenantId:guid}/{originDocumentId:guid}")]
    public async Task<IActionResult> ResolveByOrigin(Guid originTenantId, Guid originDocumentId, CancellationToken cancellationToken)
    {
        if (!await _access.HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        var doc = await _dbContext.Documents
            .Where(d => d.OriginTenantId == originTenantId && d.OriginDocumentId == originDocumentId)
            .Select(d => new { d.Id, d.Name, d.ConcurrencyToken })
            .SingleOrDefaultAsync(cancellationToken);
        if (doc is null)
        {
            return NotFound();
        }

        SetETag(doc.ConcurrencyToken);
        return Ok(new DocumentsController.DocumentResource
        {
            Id = doc.Id,
            Name = doc.Name,
            Links = [new Link("self", $"/api/documents/{doc.Id}", "GET")],
        });
    }

    [HttpHead("/api/documents/by-origin/{originTenantId:guid}/{originDocumentId:guid}")]
    public async Task<IActionResult> HeadByOrigin(Guid originTenantId, Guid originDocumentId, CancellationToken cancellationToken)
    {
        if (!await _access.HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        var doc = await _dbContext.Documents
            .Where(d => d.OriginTenantId == originTenantId && d.OriginDocumentId == originDocumentId)
            .Select(d => new { d.ConcurrencyToken })
            .SingleOrDefaultAsync(cancellationToken);
        if (doc is null)
        {
            return NotFound();
        }

        SetETag(doc.ConcurrencyToken);
        return NoContent();
    }

    private void SetETag(Guid concurrencyToken)
    {
        Response.Headers.ETag = $"\"{concurrencyToken}\"";
    }

    private static bool TryParseETag(string headerValue, out Guid token)
    {
        return Guid.TryParse(headerValue.Trim('"'), out token);
    }
}
