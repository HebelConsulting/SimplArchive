using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
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
/// Repository export/import (ADRs "Repository export"/"Repository import") — a subtree as a downloadable
/// archive, and grafting one back in. Split out of DocumentsController (#466); routes unchanged.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class DocumentTransferController : ControllerBase
{
    private readonly IAuditRecorder _audit;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly Documents.DocumentAccessService _access;
    private readonly Documents.RepositoryExporter _exporter;
    private readonly Documents.RepositoryImporter _importer;

    public DocumentTransferController(
        IAuditRecorder audit,
        ICurrentUserAccessor currentUserAccessor,
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        Documents.RepositoryExporter exporter,
        Documents.RepositoryImporter importer)
    {
        _audit = audit;
        _currentUserAccessor = currentUserAccessor;
        _dbContext = dbContext;
        _access = access;
        _exporter = exporter;
        _importer = importer;
    }

    // Exports this document (a repository root or any sub-folder) + its subtree to a downloadable .zip an import
    // can consume (ADR "Repository export"). Requires CanExport (ADR "Dedicated CanExport/CanImport rights") — a
    // bulk read that also dumps principal identities + mask definitions, delegable without full admin.
    // Streamed straight to the response body (like the audit NDJSON export; application/zip
    // isn't rewritten by VersionedContentTypeMiddleware). Filters: document-date range, filing (archival) date
    // range, all-versions vs active-only, and creator name.
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        Guid documentId,
        [FromQuery] DateOnly? documentDateFrom,
        [FromQuery] DateOnly? documentDateTo,
        [FromQuery] DateTimeOffset? filedFrom,
        [FromQuery] DateTimeOffset? filedTo,
        [FromQuery] string? versions,
        [FromQuery] string? createdBy,
        [FromQuery] bool includePermissions,
        CancellationToken cancellationToken)
    {
        if (!await _access.HasExportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        var root = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (root is null)
        {
            return NotFound();
        }

        var selection = string.Equals(versions, "active", StringComparison.OrdinalIgnoreCase)
            ? Documents.ExportVersionSelection.ActiveOnly
            : Documents.ExportVersionSelection.All;
        var filters = new Documents.RepositoryExportFilters(documentDateFrom, documentDateTo, filedFrom, filedTo, selection, createdBy);

        var fileName = $"{SanitizeFileName(root.Name)}-export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        // ZipArchive writes (incl. its central directory at dispose) are synchronous, which Kestrel disallows by
        // default — allow it for this streamed-to-the-body export so the archive isn't buffered whole in memory.
        if (HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } bodyControl)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        await _exporter.ExportAsync(documentId, filters, includePermissions, Response.Body, cancellationToken);
        return new EmptyResult();
    }

    [HttpHead("export")]
    public async Task<IActionResult> ExportHead(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _access.HasExportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        return await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken) ? NoContent() : NotFound();
    }

    // Imports an export archive (ADR "Repository import") grafted as a new sub-tree under this folder. Requires
    // CanImport (ADR "Dedicated CanExport/CanImport rights"). The root is auto-renamed if its name collides with
    // an existing child.
    // A real migration archive can be gigabytes, so lift the default 30 MB Kestrel + multipart limits (CanImport
    // gates it; the large IFormFile buffers to a temp file, not memory).
    [HttpPost("import")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Import(Guid documentId, IFormFile file, [FromQuery] bool updateExisting, [FromQuery] bool includePermissions, [FromQuery] bool merge, [FromQuery] string? leafConflict, CancellationToken cancellationToken)
    {
        if (!await _access.HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        var result = await RunImportAsync(file, documentId, updateExisting, includePermissions, merge, ParseLeafMode(leafConflict), cancellationToken);
        return Ok(result);
    }

    // The leaf-conflict mode for a merge-import (ADR "Leaf-document merge modes"); default Rename (backward-compatible).
    private static Documents.LeafMergeMode ParseLeafMode(string? value) => value?.ToLowerInvariant() switch
    {
        "newversion" => Documents.LeafMergeMode.NewVersion,
        "skip" => Documents.LeafMergeMode.Skip,
        null or "" or "rename" => Documents.LeafMergeMode.Rename,
        _ => throw new InvalidLeafConflictException(),
    };

    // Shared by the graft/merge-under-folder import (here) and the new-repository import (RepositoriesController).
    internal async Task<object> RunImportAsync(IFormFile? file, Guid? targetFolderId, bool updateExisting, bool includePermissions, bool merge, Documents.LeafMergeMode leafMode, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new NoFileException();
        }

        _importer.SetImporter(_currentUserAccessor.UserId);
        await using var stream = file.OpenReadStream();
        var result = await _importer.ImportAsync(stream, targetFolderId, updateExisting, includePermissions, merge, leafMode, cancellationToken);

        await _audit.RecordAsync(AuditActions.DocumentImported, "Document", result.RootDocumentId, result.RootName, $"{result.Documents} documents, {result.Versions} versions, {result.Skipped} already imported", cancellationToken: cancellationToken);

        return new
        {
            rootId = result.RootDocumentId,
            rootName = result.RootName,
            documents = result.Documents,
            versions = result.Versions,
            comments = result.Comments,
            skipped = result.Skipped,
            links = new[] { new Link("self", Url.Action(nameof(DocumentsController.Get), "Documents", new { documentId = result.RootDocumentId })!, "GET") },
        };
    }

    // Reduces a document name to a safe download-filename stem (the header value can't carry quotes/newlines).
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "export" : cleaned;
    }
}
