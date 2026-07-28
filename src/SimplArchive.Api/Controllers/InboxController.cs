using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Inbox;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Errors.Exceptions.Storage;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The per-user S3-backed inbox (ADR "S3-backed inbox"): raw files staged under
/// `tenants/{tenantId}/users/{userId}/inbox/` (a sub-folder of the per-user private space, ADR "Per-user
/// object-storage prefix") — no DB entity. The clients list/upload/delete items; filing an
/// item creates a real Document + Confirmed version by moving the object to a normal document key (a
/// server-side S3 copy, no re-upload) and running the same auto-classifying finalize path. Scoped to the
/// caller's userId from the token — a ServiceAccount has no inbox.
///
/// An item can carry a staged mask/index-data draft, stored as a sidecar object `{name}.mask.json` alongside
/// it (ADR "Inbox item classification + preview"). Sidecars are hidden from the listing; an item's `hasMask`
/// flag tells the client whether one exists (the desktop shows un-masked items in square brackets). Preview,
/// preview-pages and text-layout mirror the version endpoints against the inbox object key — the
/// rendition/text-layout services are keyed purely by object key, so no Document is needed.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/inbox")]
[Authorize]
public class InboxController : ControllerBase
{
    private static readonly TimeSpan PresignedUrlExpiry = TimeSpan.FromMinutes(15);
    private const string MaskSidecarSuffix = ".mask.json";

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IDocumentPreviewService _documentPreviewService;
    private readonly IDocumentTextLayoutService _textLayoutService;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly DocumentFinalizer _finalizer;

    public InboxController(
        SimplArchiveDbContext dbContext,
        IObjectStorageClient objectStorageClient,
        IDocumentPreviewService documentPreviewService,
        IDocumentTextLayoutService textLayoutService,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentUserAccessor currentUserAccessor,
        DocumentFinalizer finalizer,
        ILegalHoldService legalHold,
        IStorageQuotaService storageQuota,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _objectStorageClient = objectStorageClient;
        _documentPreviewService = documentPreviewService;
        _textLayoutService = textLayoutService;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentTenantAccessor = currentTenantAccessor;
        _currentUserAccessor = currentUserAccessor;
        _finalizer = finalizer;
        _legalHold = legalHold;
        _storageQuota = storageQuota;
        _audit = audit;
    }

    private readonly ILegalHoldService _legalHold;
    private readonly IStorageQuotaService _storageQuota;
    private readonly IAuditRecorder _audit;

    public class InboxItemResource : HypermediaResource
    {
        public string Name { get; set; } = "";

        public long Size { get; set; }

        public DateTimeOffset LastModified { get; set; }

        // True when a `{name}.mask.json` sidecar exists — the item has a staged mask/index-data draft.
        public bool HasMask { get; set; }
    }

    public class InboxResource : HypermediaResource
    {
        public List<InboxItemResource> Items { get; set; } = [];
    }

    public class UploadInboxRequest
    {
        public string FileName { get; set; } = "";
    }

    public class UploadInboxResource : HypermediaResource
    {
        public string Name { get; set; } = "";

        public Uri UploadUrl { get; set; } = null!;
    }

    public class FileInboxRequest
    {
        public Guid FolderId { get; set; }

        // When set, the item is filed as a new *version* of this existing document instead of as a new document
        // in FolderId (ADR "Context-aware inbox filing dialog").
        public Guid? DocumentId { get; set; }

        // Optional override for the filed document's name; defaults to the inbox filename.
        public string? Name { get; set; }

        // Optional feed comment posted on the resulting document (ADR "Filing posts a feed comment"); when
        // blank, a default "@{DisplayName} filed a new document." is posted.
        public string? Comment { get; set; }
    }

    public class InboxPreviewResource : HypermediaResource
    {
        public string? PreviewUrl { get; set; }

        public bool PreviewConverted { get; set; }
    }

    public class InboxPreviewPagesResource : HypermediaResource
    {
        public bool Converted { get; set; }

        public List<InboxPreviewPageResource> Pages { get; set; } = [];
    }

    public class InboxPreviewPageResource
    {
        public string Url { get; set; } = "";
    }

    public class InboxTextLayoutResource : HypermediaResource
    {
        public List<InboxTextLayoutPageResource> Pages { get; set; } = [];
    }

    public class InboxTextLayoutPageResource
    {
        public List<InboxTextLayoutWordResource> Words { get; set; } = [];
    }

    public class InboxTextLayoutWordResource
    {
        public string Text { get; set; } = "";

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }

    // The staged mask draft (also the on-the-wire shape and the sidecar JSON shape). MaskId null = "(No mask)".
    // Name/DocumentDate are staged system fields (the filed Document.Name / DocumentVersion.DocumentDate) —
    // DocumentDate is a "yyyy-MM-dd" string (ADR "Staged Name + Document date on inbox items").
    public class InboxMaskResource : HypermediaResource
    {
        public string? Name { get; set; }

        public string? DocumentDate { get; set; }

        public Guid? MaskId { get; set; }

        public List<InboxMaskFieldResource> Fields { get; set; } = [];
    }

    public class InboxMaskFieldResource
    {
        public Guid FieldDefinitionId { get; set; }

        public List<string> Values { get; set; } = [];
    }

    private (Guid TenantId, Guid UserId)? Scope() =>
        _currentTenantAccessor.TenantId is { } tenantId && _currentUserAccessor.UserId is { } userId ? (tenantId, userId) : null;

    private static string Prefix(Guid tenantId, Guid userId) => $"tenants/{tenantId}/users/{userId}/inbox/";

    private static bool IsMaskSidecar(string name) => name.EndsWith(MaskSidecarSuffix, StringComparison.OrdinalIgnoreCase);

    private static string SidecarName(string name) => name + MaskSidecarSuffix;

    // Preview renditions + the text-layout sidecar are cached next to the item (`<stem>.preview.*`,
    // `<stem>.textlayout.json` — ADR "Server-side preview renditions"/"Search hit overlay"). They must never
    // appear as inbox items, and are swept when the item leaves the inbox (ADR "Avoid inbox preview litter").
    private static bool IsDerivedArtifact(string name) =>
        name.Contains(".preview.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".textlayout.json", StringComparison.OrdinalIgnoreCase);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId))
        {
            return Forbid();
        }

        var prefix = Prefix(tenantId, userId);
        var objects = await _objectStorageClient.ListObjectsAsync(prefix, cancellationToken);

        // Names present in the prefix (used to answer "does this item have a mask sidecar?").
        var names = objects
            .Select(o => o.Key[prefix.Length..])
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.Ordinal);

        var items = new List<InboxItemResource>();
        foreach (var storageObject in objects.OrderByDescending(o => o.LastModified))
        {
            var name = storageObject.Key[prefix.Length..];
            if (string.IsNullOrEmpty(name) || IsMaskSidecar(name) || IsDerivedArtifact(name))
            {
                continue; // the prefix placeholder, a hidden mask sidecar, or a cached preview/text-layout artifact
            }

            var download = await _objectStorageClient.GetPresignedDownloadUrlAsync(storageObject.Key, PresignedUrlExpiry, name, cancellationToken);

            items.Add(new InboxItemResource
            {
                Name = name,
                Size = storageObject.Size,
                LastModified = storageObject.LastModified,
                HasMask = names.Contains(SidecarName(name)),
                Links =
                [
                    new Link("download", download.ToString(), "GET"),
                    new Link("preview", $"/api/inbox/{Uri.EscapeDataString(name)}/preview", "GET"),
                    new Link("mask", $"/api/inbox/{Uri.EscapeDataString(name)}/mask", "GET"),
                    new Link("file", $"/api/inbox/{Uri.EscapeDataString(name)}/file", "POST"),
                    new Link("self", $"/api/inbox/{Uri.EscapeDataString(name)}", "DELETE"),
                ],
            });
        }

        return Ok(new InboxResource { Items = items, Links = [new Link("self", "/api/inbox", "GET")] });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => Scope() is null ? Forbid() : NoContent();

    // Returns a presigned PUT URL so the client uploads a file straight into the inbox prefix (the Api never
    // proxies bytes). MinIO CORS (the same wildcard the drag-drop upload uses) allows the browser PUT.
    [HttpPost]
    public async Task<IActionResult> Upload([FromBody] UploadInboxRequest request, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId))
        {
            return Forbid();
        }

        var name = Path.GetFileName(request.FileName?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InboxFilenameRequiredException();
        }

        var key = Prefix(tenantId, userId) + name;
        var uploadUrl = await _objectStorageClient.GetPresignedUploadUrlAsync(key, PresignedUrlExpiry, cancellationToken);

        return Ok(new UploadInboxResource
        {
            Name = name,
            UploadUrl = uploadUrl,
            Links = [new Link("self", "/api/inbox", "GET")],
        });
    }

    // Inline preview for the item, via the rendition service on the inbox object key (renditions for TIFF/
    // office/email, else the object shown as-is). 204 when no preview is available.
    [HttpGet("{name}/preview")]
    public async Task<IActionResult> Preview(string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        var key = Prefix(tenantId, userId) + name;
        if (!await _objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        var preview = await _documentPreviewService.GetPreviewUrlAsync(key, PresignedUrlExpiry, name, cancellationToken);
        if (preview is null)
        {
            return NoContent();
        }

        return Ok(new InboxPreviewResource
        {
            PreviewUrl = preview.Url.ToString(),
            PreviewConverted = preview.IsConverted,
            Links =
            [
                new Link("self", $"/api/inbox/{Uri.EscapeDataString(name)}/preview", "GET"),
                new Link("preview-pages", $"/api/inbox/{Uri.EscapeDataString(name)}/preview-pages", "GET"),
                new Link("text-layout", $"/api/inbox/{Uri.EscapeDataString(name)}/text-layout", "GET"),
            ],
        });
    }

    [HttpHead("{name}/preview")]
    public async Task<IActionResult> PreviewHead(string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        return await _objectStorageClient.ExistsAsync(Prefix(tenantId, userId) + name, cancellationToken) ? NoContent() : NotFound();
    }

    // Ordered per-page image URLs for a multi-page TIFF; 204 for every other format (the client uses `preview`).
    [HttpGet("{name}/preview-pages")]
    public async Task<IActionResult> PreviewPages(string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        var key = Prefix(tenantId, userId) + name;
        if (!await _objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        var pages = await _documentPreviewService.GetPreviewPagesAsync(key, PresignedUrlExpiry, cancellationToken: cancellationToken);
        if (pages is null)
        {
            return NoContent();
        }

        return Ok(new InboxPreviewPagesResource
        {
            Converted = pages.IsConverted,
            Pages = pages.Urls.Select(u => new InboxPreviewPageResource { Url = u.ToString() }).ToList(),
            Links = [new Link("self", $"/api/inbox/{Uri.EscapeDataString(name)}/preview-pages", "GET")],
        });
    }

    [HttpHead("{name}/preview-pages")]
    public async Task<IActionResult> PreviewPagesHead(string name, CancellationToken cancellationToken) =>
        await PreviewHead(name, cancellationToken);

    // Per-page word boxes for hit-overlay / find-in-document, via the text-layout service on the object key.
    [HttpGet("{name}/text-layout")]
    public async Task<IActionResult> TextLayout(string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        var key = Prefix(tenantId, userId) + name;
        if (!await _objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        var layout = await _textLayoutService.GetTextLayoutAsync(key, cancellationToken);
        if (layout is null)
        {
            return NoContent();
        }

        return Ok(new InboxTextLayoutResource
        {
            Pages = layout.Pages
                .Select(p => new InboxTextLayoutPageResource
                {
                    Words = p.Words
                        .Select(w => new InboxTextLayoutWordResource { Text = w.Text, X = w.X, Y = w.Y, Width = w.Width, Height = w.Height })
                        .ToList(),
                })
                .ToList(),
            Links = [new Link("self", $"/api/inbox/{Uri.EscapeDataString(name)}/text-layout", "GET")],
        });
    }

    [HttpHead("{name}/text-layout")]
    public async Task<IActionResult> TextLayoutHead(string name, CancellationToken cancellationToken) =>
        await PreviewHead(name, cancellationToken);

    // Reads the staged mask/index-data draft from the `{name}.mask.json` sidecar; an empty draft (no sidecar).
    [HttpGet("{name}/mask")]
    public async Task<IActionResult> GetMask(string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        var itemKey = Prefix(tenantId, userId) + name;
        if (!await _objectStorageClient.ExistsAsync(itemKey, cancellationToken))
        {
            return NotFound();
        }

        var draft = await ReadMaskSidecarAsync(tenantId, userId, name, cancellationToken) ?? new InboxMaskResource();
        draft.Links = [new Link("self", $"/api/inbox/{Uri.EscapeDataString(name)}/mask", "GET")];
        return Ok(draft);
    }

    [HttpHead("{name}/mask")]
    public async Task<IActionResult> GetMaskHead(string name, CancellationToken cancellationToken) =>
        await PreviewHead(name, cancellationToken);

    // Writes (or, for "(No mask)", clears) the staged mask/index-data draft sidecar. A staging draft, not a
    // filed document, so no required-field/format validation runs here — that happens if/when the item is filed.
    [HttpPut("{name}/mask")]
    public async Task<IActionResult> SetMask(string name, [FromBody] InboxMaskResource request, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        var itemKey = Prefix(tenantId, userId) + name;
        if (!await _objectStorageClient.ExistsAsync(itemKey, cancellationToken))
        {
            return NotFound();
        }

        var sidecarKey = Prefix(tenantId, userId) + SidecarName(name);

        // No mask, no field values, no name and no date → nothing staged, so remove the sidecar and the item
        // reads as un-classified (square brackets).
        if (request.MaskId is null && request.Fields.All(f => f.Values.Count == 0)
            && string.IsNullOrWhiteSpace(request.Name) && string.IsNullOrWhiteSpace(request.DocumentDate))
        {
            if (await _objectStorageClient.ExistsAsync(sidecarKey, cancellationToken))
            {
                await _objectStorageClient.DeleteObjectAsync(sidecarKey, cancellationToken);
            }

            return NoContent();
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new InboxMaskResource
        {
            Name = request.Name,
            DocumentDate = request.DocumentDate,
            MaskId = request.MaskId,
            Fields = request.Fields,
        });
        using var stream = new MemoryStream(payload);
        await _objectStorageClient.PutObjectAsync(sidecarKey, stream, "application/json", cancellationToken);
        return NoContent();
    }

    private async Task<InboxMaskResource?> ReadMaskSidecarAsync(Guid tenantId, Guid userId, string name, CancellationToken cancellationToken)
    {
        var sidecarKey = Prefix(tenantId, userId) + SidecarName(name);
        if (!await _objectStorageClient.ExistsAsync(sidecarKey, cancellationToken))
        {
            return null;
        }

        await using var stream = await _objectStorageClient.GetObjectAsync(sidecarKey, cancellationToken);
        return await JsonSerializer.DeserializeAsync<InboxMaskResource>(stream, cancellationToken: cancellationToken);
    }

    // Files an inbox item into a repository folder: moves its object to a normal document key (server-side
    // copy + delete) and creates a Document + Confirmed version via the shared auto-classifying finalize path.
    [HttpPost("{name}/file")]
    public async Task<IActionResult> File(string name, [FromBody] FileInboxRequest request, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        var inboxKey = Prefix(tenantId, userId) + name;
        if (!await _objectStorageClient.ExistsAsync(inboxKey, cancellationToken))
        {
            return NotFound();
        }

        // Storage-quota enforcement (ADR "Per-tenant storage quota"): reject filing that would push the tenant past
        // its quota BEFORE the object is moved out of the inbox, so the item is preserved on rejection. Covers both
        // file-into-folder and file-as-version (each adds a confirmed blob).
        var inboxSizeBytes = await _objectStorageClient.GetObjectSizeAsync(inboxKey, cancellationToken);
        if (!await _storageQuota.CanStoreAsync(tenantId, inboxSizeBytes, cancellationToken))
        {
            throw new StorageQuotaExceededException("Filing this item would exceed the tenant's storage quota.");
        }

        // File as a new version of an existing document instead of as a new document in a folder.
        if (request.DocumentId is { } targetDocumentId)
        {
            return await FileAsVersionAsync(tenantId, userId, name, inboxKey, targetDocumentId, request.Comment, cancellationToken);
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == request.FolderId, cancellationToken))
        {
            throw new FolderNotFoundException();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, request.FolderId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        // Split the name: the inbox file's extension goes on the object key, the stem becomes Document.Name
        // (ADR "Extension off Document.Name, derived from the object key").
        var rawName = string.IsNullOrWhiteSpace(request.Name) ? name : request.Name.Trim();
        var extension = Path.GetExtension(name);
        var documentName = Path.GetFileNameWithoutExtension(rawName);
        var now = DateTimeOffset.UtcNow;

        // Consume the staged classification draft, if any (ADR "Consume the staged mask sidecar at filing").
        // Emails are never staged (they aren't offered a mask in the inbox) — they always auto-classify.
        var isEmail = extension is ".eml" or ".msg";
        StagedClassification? staged = null;
        if (!isEmail && await ReadMaskSidecarAsync(tenantId, userId, name, cancellationToken) is { } draft)
        {
            staged = new StagedClassification(
                draft.Name, draft.DocumentDate, draft.MaskId,
                draft.Fields.Select(f => (f.FieldDefinitionId, (IReadOnlyList<string>)f.Values)).ToList());
        }

        // Move the object out of the inbox to a normal document key (server-side copy within the bucket).
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, extension);
        await _objectStorageClient.CopyObjectAsync(inboxKey, objectKey, cancellationToken);
        await _objectStorageClient.DeleteObjectAsync(inboxKey, cancellationToken);

        var documentId = Guid.NewGuid();
        var document = new Document
        {
            Id = documentId,
            TenantId = tenantId,
            ParentId = request.FolderId,
            Name = documentName,
            CreatedByUserId = userId,
            CreatedAt = now,
        };

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };

        _dbContext.Documents.Add(document);
        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Confirm + classify (the object is already in storage). A staged draft applies the user's inbox
        // classification; otherwise the normal auto-classification runs — same path as a normal upload.
        await _finalizer.FinalizeAsync(version, cancellationToken, staged);

        // The item left the inbox — sweep its staged-mask sidecar + cached preview artifacts so they don't orphan.
        await PurgeItemArtifactsAsync(tenantId, userId, name, cancellationToken);
        await PostFilingCommentAsync(tenantId, userId, documentId, request.Comment, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentFiled, "Document", documentId, document.Name, "Filed from inbox as a new document", cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId }, new { id = documentId, name = document.Name });
    }

    // Files the inbox item as the next Confirmed version of an existing document (ADR "Context-aware inbox
    // filing dialog"): moves the object to a document key and finalizes a new version. The document keeps its
    // existing classification (no re-classify, and a staged sidecar is ignored — it's an existing document).
    private async Task<IActionResult> FileAsVersionAsync(Guid tenantId, Guid userId, string name, string inboxKey, Guid documentId, string? comment, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            throw DocumentNotFoundException.InvalidFilingTarget();
        }

        // Adding a version edits the document's content.
        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        // A legal hold freezes new versions too (ADR "Legal hold & retention enforcement").
        if (await _legalHold.IsFrozenAsync(documentId, cancellationToken))
        {
            throw new DocumentUnderLegalHoldException();
        }

        // A check-out by another user blocks filing a new version too (ADR "Document check-out / check-in").
        var checkoutHolder = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.CheckedOutByUserId).FirstOrDefaultAsync(cancellationToken);
        if (checkoutHolder is { } h && h != userId)
        {
            throw new DocumentCheckedOutException();
        }

        var now = DateTimeOffset.UtcNow;
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, Path.GetExtension(name));
        await _objectStorageClient.CopyObjectAsync(inboxKey, objectKey, cancellationToken);
        await _objectStorageClient.DeleteObjectAsync(inboxKey, cancellationToken);

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };

        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _finalizer.FinalizeAsync(version, cancellationToken); // no staged draft — existing document keeps its mask
        await PurgeItemArtifactsAsync(tenantId, userId, name, cancellationToken);
        await PostFilingCommentAsync(tenantId, userId, documentId, comment, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentFiled, "Document", documentId, document.Name, "Filed from inbox as a new version", cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId }, new { id = documentId, name = document.Name });
    }

    // Posts a feed comment on the filed document (ADR "Filing posts a feed comment"): the caller's comment
    // verbatim, or a default "Filed a new document." when none was given (the author name is already shown in
    // the feed, so the body doesn't repeat it). Best-effort — a filed document shouldn't fail over its feed entry.
    private async Task PostFilingCommentAsync(Guid tenantId, Guid userId, Guid documentId, string? comment, CancellationToken cancellationToken)
    {
        try
        {
            var body = string.IsNullOrWhiteSpace(comment) ? "Filed a new document." : comment.Trim();

            _dbContext.DocumentComments.Add(new DocumentComment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DocumentId = documentId,
                Body = body,
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Best-effort — the document is filed regardless of the feed comment.
        }
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId) || IsMaskSidecar(name))
        {
            return Forbid();
        }

        var key = Prefix(tenantId, userId) + name;
        if (!await _objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        await _objectStorageClient.DeleteObjectAsync(key, cancellationToken);
        await PurgeItemArtifactsAsync(tenantId, userId, name, cancellationToken);
        return NoContent();
    }

    // Sweeps an item's derived objects when it leaves the inbox: its `{name}.mask.json` staging sidecar plus
    // every cached preview/text-layout artifact sharing its stem (`<stem>.preview.*`, `<stem>.textlayout.json`).
    private async Task PurgeItemArtifactsAsync(Guid tenantId, Guid userId, string name, CancellationToken cancellationToken)
    {
        var prefix = Prefix(tenantId, userId);
        var lastDot = name.LastIndexOf('.');
        var stem = lastDot >= 0 ? name[..lastDot] : name;

        foreach (var storageObject in await _objectStorageClient.ListObjectsAsync(prefix, cancellationToken))
        {
            var candidate = storageObject.Key[prefix.Length..];
            var isArtifact = candidate == SidecarName(name)
                || candidate.StartsWith($"{stem}.preview.", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals($"{stem}.textlayout.json", StringComparison.OrdinalIgnoreCase);
            if (isArtifact)
            {
                await _objectStorageClient.DeleteObjectAsync(storageObject.Key, cancellationToken);
            }
        }
    }
}
