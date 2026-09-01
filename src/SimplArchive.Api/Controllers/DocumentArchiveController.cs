using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Archive;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Browses a .zip document's contents on demand without unpacking it (ADR "Zip file browsing"): a zip is
/// stored as-is (no child documents, unlike email attachments), and its entries are read from object storage
/// each time. Requires CanReadContent, like downloading a version. GET lists the entries; GET content streams
/// one entry's bytes — the one place the Api proxies file bytes (entries aren't storage objects), capped
/// against decompression bombs.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/archive-entries")]
[Authorize]
public class DocumentArchiveController : ControllerBase
{
    private const long MaxArchiveBytes = 200L * 1024 * 1024; // 200 MB zip buffered into memory

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IArchiveReader _archiveReader;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public DocumentArchiveController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        IObjectStorageClient objectStorageClient,
        IArchiveReader archiveReader,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _objectStorageClient = objectStorageClient;
        _archiveReader = archiveReader;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
    }

    public class ArchiveEntryResource : HypermediaResource
    {
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public long Size { get; set; }
    }

    public class ArchiveEntriesResource : HypermediaResource
    {
        public List<ArchiveEntryResource> Entries { get; set; } = [];
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid documentId, CancellationToken cancellationToken)
    {
        var objectKey = await ResolveZipObjectKeyAsync(documentId, cancellationToken);
        if (objectKey is null)
        {
            return NotFound();
        }

        if (!await CanReadContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (!IsZip(objectKey))
        {
            throw new NotAnArchiveException();
        }

        await using var buffer = await BufferAsync(objectKey, cancellationToken);
        var entries = _archiveReader.ListEntries(buffer);

        var resource = new ArchiveEntriesResource
        {
            Entries = entries.Select(e => new ArchiveEntryResource
            {
                Name = e.Name,
                Path = e.Path,
                Size = e.Size,
                Links = [new Link("download", $"/api/documents/{documentId}/archive-entries/content?path={Uri.EscapeDataString(e.Path)}", "GET")],
            }).ToList(),
            Links = [new Link("self", $"/api/documents/{documentId}/archive-entries", "GET")],
        };

        return Ok(resource);
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        var objectKey = await ResolveZipObjectKeyAsync(documentId, cancellationToken);
        if (objectKey is null)
        {
            return NotFound();
        }

        if (!await CanReadContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return IsZip(objectKey)
            ? NoContent()
            : throw new NotAnArchiveException();
    }

    [HttpGet("content")]
    public async Task<IActionResult> DownloadEntry(Guid documentId, [FromQuery] string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArchiveEntryRequiredException();
        }

        var objectKey = await ResolveZipObjectKeyAsync(documentId, cancellationToken);
        if (objectKey is null)
        {
            return NotFound();
        }

        if (!await CanReadContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (!IsZip(objectKey))
        {
            throw new NotAnArchiveException();
        }

        await using var buffer = await BufferAsync(objectKey, cancellationToken);

        byte[]? bytes;
        try
        {
            bytes = _archiveReader.ReadEntry(buffer, path);
        }
        catch (InvalidOperationException e)
        {
            throw new ArchiveEntryTooLargeException(e.Message);
        }

        if (bytes is null)
        {
            return NotFound();
        }

        // The Api proxies these bytes deliberately — an archive entry isn't a storage object with a presigned
        // URL (ADR "Zip file browsing").
        return File(bytes, "application/octet-stream", System.IO.Path.GetFileName(path));
    }

    // Buffers the stored zip into a seekable MemoryStream (ZipArchive needs random access), capping the size.
    private async Task<MemoryStream> BufferAsync(string objectKey, CancellationToken cancellationToken)
    {
        await using var source = await _objectStorageClient.GetObjectAsync(objectKey, cancellationToken);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxArchiveBytes)
            {
                await buffer.DisposeAsync();
                throw new ArchiveTooLargeException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        buffer.Position = 0;
        return buffer;
    }

    private async Task<string?> ResolveZipObjectKeyAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var doc = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.CurrentVersionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (doc is null)
        {
            return null;
        }

        // The current version honoring the CurrentVersionId pointer (issue #265), else the latest confirmed.
        var current = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, doc.CurrentVersionId, cancellationToken);
        return current?.ObjectKey;
    }

    private static bool IsZip(string objectKey) =>
        System.IO.Path.GetExtension(objectKey).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    // ServiceAccount first, then a logged-in User (mutually exclusive per request), same as
    // DocumentVersionsController — see ADR "Document-scope authorization retrofit for User".
    private async Task<bool> CanReadContentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (await _effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken)).CanReadContent;
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanReadContent;
        }

        return false;
    }
}
