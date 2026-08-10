using System.Globalization;
using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Errors.Exceptions.Storage;
using SimplArchive.Api.Errors.Exceptions.Workflow;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "File upload / download API design" for Document content — see ADR
/// "DocumentVersionsController resource-oriented redesign" for why this uses POST (create) / PUT
/// (finalize, idempotent) / GET+HEAD (read a version, with a "download" link when confirmed) instead of
/// verb-phrase routes. Authorization checks are Document-scope, and accept either a ServiceAccount or a
/// logged-in User caller (see ADR "Document-scope authorization retrofit for User, and
/// tenant-administrator-driven onboarding") — every check is against the Document the version belongs to,
/// not its Repository.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/versions")]
[Authorize]
public class DocumentVersionsController : ControllerBase
{
    private static readonly TimeSpan PresignedUrlExpiry = TimeSpan.FromMinutes(15);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IDocumentPreviewService _documentPreviewService;
    private readonly IDocumentTextLayoutService _textLayoutService;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public DocumentVersionsController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        IObjectStorageClient objectStorageClient,
        IDocumentPreviewService documentPreviewService,
        IDocumentTextLayoutService textLayoutService,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IDocumentIndexQueue queue,
        DocumentFinalizer finalizer,
        Documents.ChatSystemEntryRecorder chatEntries,
        ILegalHoldService legalHold,
        IWormLockService wormLock,
        IStorageQuotaService storageQuota,
        IAuditRecorder audit,
        IUserSystemRightsResolver userSystemRights,
        IDocumentVersionComparer comparer)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _objectStorageClient = objectStorageClient;
        _documentPreviewService = documentPreviewService;
        _textLayoutService = textLayoutService;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _queue = queue;
        _finalizer = finalizer;
        _chatEntries = chatEntries;
        _legalHold = legalHold;
        _wormLock = wormLock;
        _storageQuota = storageQuota;
        _audit = audit;
        _userSystemRights = userSystemRights;
        _comparer = comparer;
    }

    private readonly IUserSystemRightsResolver _userSystemRights;

    // The caller's effective CanImport (ADR 0403/0520) — a User's own-∪-groups rights or a ServiceAccount's column.
    // Gates backdating a version's filing date (a past FiledAt); an omitted/now FiledAt needs only CanEditContent.
    private async Task<bool> HasImportRightAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanImport;
        }

        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts.Where(s => s.Id == serviceAccountId).Select(s => s.CanImport).SingleOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    private readonly IDocumentVersionComparer _comparer;
    private readonly IAuditRecorder _audit;
    private readonly IDocumentIndexQueue _queue;
    private readonly ILegalHoldService _legalHold;
    private readonly IWormLockService _wormLock;
    private readonly IStorageQuotaService _storageQuota;

    // Refuses a new version / metadata change on a document frozen by an active legal hold (ADR "Legal hold &
    // retention enforcement").
    private async Task EnsureNotFrozenAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (await _legalHold.IsFrozenAsync(documentId, cancellationToken))
        {
            throw new DocumentUnderLegalHoldException();
        }
    }

    // Refuses a new version / metadata change on a document checked out by a DIFFERENT user (ADR "Document
    // check-out / check-in"). The holder proceeds — the desktop check-in uploads the new version while holding
    // the lock, then releases it.
    private async Task EnsureNotCheckedOutByOtherAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var holder = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.CheckedOutByUserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (holder is { } h && h != _currentUserAccessor.UserId)
        {
            throw new DocumentCheckedOutException();
        }
    }
    private readonly DocumentFinalizer _finalizer;
    private readonly Documents.ChatSystemEntryRecorder _chatEntries;

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class CreateVersionResponse : HypermediaResource
    {
        public Guid Id { get; set; }

        public string ObjectKey { get; set; } = "";

        public Uri UploadUrl { get; set; } = null!;
    }

    // Optional body. DocumentDate is the issuing date ("yyyy-MM-dd") — omitted, it defaults to the version's
    // filing date (CreatedAt). FileExtension carries the uploaded file's extension (e.g. ".tif") so the object
    // key keeps the correct type now that Document.Name no longer holds it (ADR "Extension off Document.Name,
    // derived from the object key"); omitted, it falls back to the document name's extension (back-compat).
    // Strings on the wire (XmlSerializer doesn't support DateOnly).
    public class CreateVersionRequest
    {
        public string? DocumentDate { get; set; }

        public string? FileExtension { get; set; }

        // Optional filing date (ADR 0520) — when supplied, drives BOTH the object-key year
        // (tenants/{t}/{filingYear}/…) and DocumentVersion.CreatedAt, so an import honours the original filing date
        // (e.g. 2003) instead of "now". Omitted → now. A PAST filing date requires CanImport (backdating).
        public string? FiledAt { get; set; }

        // Optional per-version comment (ADR 0528) — the "why this revision" note, shown in the versions dialog.
        public string? Comment { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> CreateVersion(Guid documentId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateVersionRequest? request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId, d.Name, d.CreatedAt, d.StorageFolderId })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await CanEditContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        // The object key carries the file extension so the stored blob keeps the correct type (ADR "Object key
        // file extension"). It comes from the request's FileExtension (the client now sends the extension
        // separately, since Document.Name no longer holds it — ADR "Extension off Document.Name"); for callers
        // that still put the extension in the name, it falls back to the name's extension.
        var fileExtension = string.IsNullOrWhiteSpace(request?.FileExtension)
            ? Path.GetExtension(document.Name)
            : request.FileExtension;
        // Filing date (ADR 0520): a supplied FiledAt drives BOTH the object-key year (tenants/{t}/{filingYear}/…)
        // and CreatedAt, so an import honours the original filing date (e.g. 2003); omitted → now. Backdating (a
        // filing date before today) is an import concern and requires CanImport.
        var now = DateTimeOffset.UtcNow;
        var filedAt = now;
        if (!string.IsNullOrWhiteSpace(request?.FiledAt))
        {
            if (!DateTimeOffset.TryParse(request.FiledAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out filedAt))
            {
                throw new InvalidFilingDateException($"'{request.FiledAt}' is not a valid filing date/time.");
            }

            if (filedAt.UtcDateTime.Date < now.UtcDateTime.Date && !await HasImportRightAsync(cancellationToken))
            {
                throw new FilingDateBackdatingRequiresImportRightException();
            }
        }

        // The key groups by the document's storage folder (ADR 0530), bucketed by the VERSION's filing year (ADR
        // 0520) — versions of one year share a folder; the new version's id is the leaf. A backdated filing date
        // (needs CanImport) therefore also drives the bucket year, matching the version's CreatedAt.
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, filedAt, document.StorageFolderId, versionId, fileExtension);
        var uploadUrl = await _objectStorageClient.GetPresignedUploadUrlAsync(objectKey, PresignedUrlExpiry, cancellationToken);

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();
        var createdAt = filedAt;

        // Issuing date: the client-supplied date, else the filing date (CreatedAt) by default.
        DateOnly documentDate;
        if (string.IsNullOrWhiteSpace(request?.DocumentDate))
        {
            documentDate = DateOnly.FromDateTime(createdAt.UtcDateTime);
        }
        else if (!DateOnly.TryParse(request.DocumentDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out documentDate))
        {
            throw new InvalidDocumentDateException($"'{request.DocumentDate}' is not a valid date (expected yyyy-MM-dd).");
        }

        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = document.TenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = createdAt,
            DocumentDate = documentDate,
            Comment = string.IsNullOrWhiteSpace(request?.Comment) ? null : request.Comment.Trim(),
        };

        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Get), new { documentId, versionId = version.Id }, new CreateVersionResponse
        {
            Id = version.Id,
            ObjectKey = version.ObjectKey,
            UploadUrl = uploadUrl,
            Links = [new Link("self", $"/api/documents/{documentId}/versions/{version.Id}", "GET")],
        });
    }

    public class DocumentVersionResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public int? VersionNumber { get; set; }

        public string ObjectKey { get; set; } = "";

        public string? Sha256Hash { get; set; }

        public string Status { get; set; } = "";

        // True when the `preview` link points at a server-generated rendition rather than the original file
        // shown as-is — the client badges it so the user knows it isn't the original (ADR "Converted-preview
        // overlay badge").
        public bool PreviewConverted { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        // The creator's display name (User.DisplayName / ServiceAccount.Name) — a read-only system field.
        public string CreatedByName { get; set; } = "";

        // The issuing date ("yyyy-MM-dd") — a string on the wire (XmlSerializer doesn't support DateOnly).
        public string DocumentDate { get; set; } = "";

        // The version's OCR-language override (Tesseract "+"-joined; null = inherit the tenant default) — the
        // system-field picker on a TIFF version (ADR "Per-tenant / per-version OCR languages").
        public string? OcrLanguages { get; set; }

        // The file extension (e.g. ".tif"), derived from the object key — a read-only system field now that
        // Document.Name no longer carries it (ADR "Extension off Document.Name, derived from the object key").
        public string FileExtension { get; set; } = "";

        // The optional per-version comment (ADR 0528) — the "why this revision" note, shown in the versions dialog.
        public string? Comment { get; set; }
    }

    private record VersionRow(
        Guid Id, Guid DocumentId, DocumentVersionStatus Status, int? VersionNumber, string ObjectKey,
        string? Sha256Hash, DateTimeOffset CreatedAt, DateOnly DocumentDate,
        Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, string? OcrLanguages, string? Comment);

    public class DocumentVersionListResource : HypermediaResource
    {
        public List<DocumentVersionResource> Versions { get; set; } = [];

        // The document's current version honoring the CurrentVersionId pointer (ADR "Version-restore via a
        // current-version pointer", issue #265) — the clients read this instead of deriving "latest confirmed"
        // themselves. Caller-aware: the pinned version if set + visible to this caller, else the caller's
        // latest visible confirmed version (so ADR 0300 gating is respected). Null when the document has none.
        public Guid? CurrentVersionId { get; set; }

        public int? CurrentVersionNumber { get; set; }
    }

    // Cursor-based pagination (?cursor=&limit=), CreatedAt ascending / Id ascending as tiebreaker — same
    // shape as every other list endpoint (ADR "Pagination for list endpoints"). Requires CanReadContent,
    // the same right as the single-version GET below. See ADR "Blazor repository/document browsing
    // (read-only tree)" — the browse detail pane needs to enumerate a document's versions, which only the
    // single-version GET could serve before.
    [HttpGet]
    public async Task<IActionResult> List(Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        // Reads serve soft-deleted (recycle-bin) documents too (ADR "Recycle bin tab") — the detail pane's
        // preview needs a deleted item's versions; auth still applies, mutations keep the filter.
        var docInfo = await _dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => d.Id == documentId)
            .Select(d => new { d.CurrentVersionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (docInfo is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanReadContent)
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.DocumentVersions.Where(v => v.DocumentId == documentId);

        // Workflow status-gating (ADR "Workflow status-gating"): hide gated versions (in a workflow, not yet
        // Released) from a caller without CanEditContent, unless they're that version's assigned reviewer — so
        // the latest *visible* version becomes their effective "current" one. EF Core null semantics make
        // `AssignedToUserId != uid` true for a resolved (null-assignee) gated version, hiding those too; a
        // caller with no user id (ServiceAccount) is never a reviewer, so every gated version is hidden.
        if (!rights.CanEditContent)
        {
            var uid = _currentUserAccessor.UserId;
            query = uid is { } reviewerId
                ? query.Where(v => !_dbContext.WorkflowStates.Any(w => w.DocumentVersionId == v.Id && w.Status != WorkflowStatus.Released && w.AssignedToUserId != reviewerId))
                : query.Where(v => !_dbContext.WorkflowStates.Any(w => w.DocumentVersionId == v.Id && w.Status != WorkflowStatus.Released));
        }

        // The caller-aware current version: the pinned version if set + visible, else the caller's latest visible
        // confirmed version (gating already applied to `query`) — issue #265.
        var current = await CurrentVersion.ResolveAsync(query, documentId, docInfo.CurrentVersionId, cancellationToken);

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(v => v.CreatedAt > cursorCreatedAt || (v.CreatedAt == cursorCreatedAt && v.Id > cursorId));
        }

        var fetched = await query
            .OrderBy(v => v.CreatedAt).ThenBy(v => v.Id)
            .Take(pageSize + 1)
            .Select(v => new VersionRow(v.Id, v.DocumentId, v.Status, v.VersionNumber, v.ObjectKey, v.Sha256Hash, v.CreatedAt, v.DocumentDate, v.CreatedByUserId, v.CreatedByServiceAccountId, v.OcrLanguages, v.Comment))
            .ToListAsync(cancellationToken);

        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(List), new { documentId, cursor, limit = pageSize })!, "GET"),
            // Comparing two of these versions — the client appends ?from=&to= to this advertised address.
            new("compare", $"/api/documents/{documentId}/versions/compare", "GET"),
        };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        var documentName = await LoadDocumentNameAsync(documentId, cancellationToken);
        var versions = new List<DocumentVersionResource>(page.Count);
        foreach (var row in page)
        {
            versions.Add(await BuildResourceAsync(row, documentName, cancellationToken));
        }

        return Ok(new DocumentVersionListResource
        {
            Versions = versions,
            Links = links,
            CurrentVersionId = current?.Id,
            CurrentVersionNumber = current?.VersionNumber,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public async Task<IActionResult> HeadList(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanReadContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpGet("{versionId:guid}")]
    public async Task<IActionResult> Get(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadForReadAsync(documentId, versionId, cancellationToken);

        if (version is null)
        {
            return NotFound();
        }

        if (!await CanAccessVersionContentAsync(version.Id, documentId, cancellationToken))
        {
            return Forbid();
        }

        var documentName = await LoadDocumentNameAsync(documentId, cancellationToken);
        return Ok(await BuildResourceAsync(version, documentName, cancellationToken));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{versionId:guid}")]
    public async Task<IActionResult> Head(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadForReadAsync(documentId, versionId, cancellationToken);

        if (version is null)
        {
            return NotFound();
        }

        if (!await CanAccessVersionContentAsync(version.Id, documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    // Inline unified text diff between two versions of this document (ADR "Document version comparison").
    // Requires CanReadContent on both (via CanAccessVersionContentAsync, which also enforces workflow gating).
    // Available is false when either version has no extractable text (a binary/image format, or Tika unavailable
    // for office/PDF) — the client then shows "comparison not available for this format".
    // The PAIR is expressed as a query, not as path segments (issue #416). A link names ONE resource, so
    // "/versions/{from}/compare/{to}" could never be advertised — the client had to build it, which is exactly
    // what ADR 0543 removes. As "/versions/compare?from=&to=" the collection advertises a single `compare`
    // address and the client supplies its two operands as parameters, the same shape as any other filter.
    [HttpGet("compare")]
    public async Task<IActionResult> Compare(Guid documentId, [FromQuery] Guid from, [FromQuery] Guid to, CancellationToken cancellationToken)
    {
        var fromVersionId = from;
        var toVersionId = to;
        var fromVersion = await LoadForReadAsync(documentId, fromVersionId, cancellationToken);
        var toVersion = await LoadForReadAsync(documentId, toVersionId, cancellationToken);
        if (fromVersion is null || toVersion is null)
        {
            return NotFound();
        }

        if (!await CanAccessVersionContentAsync(fromVersion.Id, documentId, cancellationToken)
            || !await CanAccessVersionContentAsync(toVersion.Id, documentId, cancellationToken))
        {
            return Forbid();
        }

        var comparison = await _comparer.CompareAsync(fromVersion.ObjectKey, toVersion.ObjectKey, cancellationToken: cancellationToken);

        return Ok(new VersionComparisonResource
        {
            FromVersionId = fromVersion.Id,
            FromVersionNumber = fromVersion.VersionNumber,
            ToVersionId = toVersion.Id,
            ToVersionNumber = toVersion.VersionNumber,
            Available = comparison.Available,
            Lines = comparison.Lines.Select(l => new DiffLineResource { Op = (int)l.Op, Text = l.Text }).ToList(),
            Links = [new Link("self", $"/api/documents/{documentId}/versions/compare?from={fromVersionId}&to={toVersionId}", "GET")],
        });
    }

    [HttpHead("compare")]
    public async Task<IActionResult> CompareHead(Guid documentId, [FromQuery] Guid from, [FromQuery] Guid to, CancellationToken cancellationToken)
    {
        var fromVersion = await LoadForReadAsync(documentId, from, cancellationToken);
        var toVersion = await LoadForReadAsync(documentId, to, cancellationToken);
        if (fromVersion is null || toVersion is null)
        {
            return NotFound();
        }

        return await CanAccessVersionContentAsync(fromVersion.Id, documentId, cancellationToken)
            && await CanAccessVersionContentAsync(toVersion.Id, documentId, cancellationToken)
            ? NoContent()
            : Forbid();
    }

    // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"). Computed/cached on demand:
    // images via OCR (Tesseract hOCR), PDFs via their text layer (PdfPig), against the same rendition the
    // client displays so the boxes line up. 204 when the version isn't confirmed or the format has no overlay
    // (plain text, a scanned PDF with no text layer, OCR unavailable). Requires CanReadContent (same as the
    // version content itself).
    [HttpGet("{versionId:guid}/text-layout")]
    public async Task<IActionResult> TextLayout(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadForReadAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        if (!await CanAccessVersionContentAsync(version.Id, documentId, cancellationToken))
        {
            return Forbid();
        }

        if (version.Status != DocumentVersionStatus.Confirmed)
        {
            return NoContent();
        }

        var layout = await _textLayoutService.GetTextLayoutAsync(version.ObjectKey, cancellationToken);
        if (layout is null)
        {
            return NoContent();
        }

        return Ok(new TextLayoutResource
        {
            Pages = layout.Pages
                .Select(p => new TextLayoutPageResource
                {
                    Words = p.Words
                        .Select(w => new TextLayoutWordResource { Text = w.Text, X = w.X, Y = w.Y, Width = w.Width, Height = w.Height })
                        .ToList(),
                })
                .ToList(),
            Links = [new Link("self", $"/api/documents/{documentId}/versions/{versionId}/text-layout", "GET")],
        });
    }

    [HttpHead("{versionId:guid}/text-layout")]
    public async Task<IActionResult> TextLayoutHead(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadForReadAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        return await CanAccessVersionContentAsync(version.Id, documentId, cancellationToken) ? NoContent() : Forbid();
    }

    // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages") — each page is
    // its own PNG rendition, so the client shows them as separate pages (like PDF pages). 204 for every other
    // format (single image / PDF / office / text), where the client uses the single `preview` link. Requires
    // CanReadContent (same as the content).
    [HttpGet("{versionId:guid}/preview-pages")]
    public async Task<IActionResult> PreviewPages(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadForReadAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        if (!await CanAccessVersionContentAsync(version.Id, documentId, cancellationToken))
        {
            return Forbid();
        }

        if (version.Status != DocumentVersionStatus.Confirmed)
        {
            return NoContent();
        }

        var pages = await _documentPreviewService.GetPreviewPagesAsync(version.ObjectKey, PresignedUrlExpiry, cancellationToken: cancellationToken);
        if (pages is null)
        {
            return NoContent();
        }

        return Ok(new PreviewPagesResource
        {
            Converted = pages.IsConverted,
            Pages = pages.Urls.Select(u => new PreviewPageResource { Url = u.ToString() }).ToList(),
            Links = [new Link("self", $"/api/documents/{documentId}/versions/{versionId}/preview-pages", "GET")],
        });
    }

    [HttpHead("{versionId:guid}/preview-pages")]
    public async Task<IActionResult> PreviewPagesHead(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadForReadAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        return await CanAccessVersionContentAsync(version.Id, documentId, cancellationToken) ? NoContent() : Forbid();
    }

    public class PreviewPagesResource : HypermediaResource
    {
        public bool Converted { get; set; }

        public List<PreviewPageResource> Pages { get; set; } = [];
    }

    public class PreviewPageResource
    {
        public string Url { get; set; } = "";
    }

    public class TextLayoutResource : HypermediaResource
    {
        public List<TextLayoutPageResource> Pages { get; set; } = [];
    }

    // Inline unified diff between two versions (ADR "Document version comparison"). Available == false → neither
    // side had extractable text (a binary/image format, or Tika unavailable) and Lines is empty.
    public class VersionComparisonResource : HypermediaResource
    {
        public Guid FromVersionId { get; set; }
        public int? FromVersionNumber { get; set; }
        public Guid ToVersionId { get; set; }
        public int? ToVersionNumber { get; set; }
        public bool Available { get; set; }
        public List<DiffLineResource> Lines { get; set; } = [];
    }

    // Op: 0 = unchanged, 1 = added, 2 = removed (matches Application's DiffOp).
    public class DiffLineResource
    {
        public int Op { get; set; }
        public string Text { get; set; } = "";
    }

    public class TextLayoutPageResource
    {
        public List<TextLayoutWordResource> Words { get; set; } = [];
    }

    // Coordinates are normalized 0..1 within the page (top-left origin) so the client scales them to whatever
    // size it renders the page at.
    public class TextLayoutWordResource
    {
        public string Text { get; set; } = "";

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }

    // Optional finalize body — carries the version comment (ADR 0528) when the caller only has it at finalize time
    // (the browser drop-upload creates the version first, then finalizes with the filing comment).
    public class FinalizeVersionRequest
    {
        public string? Comment { get; set; }
    }

    [HttpPut("{versionId:guid}")]
    public async Task<IActionResult> Finalize(Guid documentId, Guid versionId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] FinalizeVersionRequest? request, CancellationToken cancellationToken)
    {
        var version = await _dbContext.DocumentVersions.SingleOrDefaultAsync(v => v.Id == versionId && v.DocumentId == documentId, cancellationToken);

        if (version is null)
        {
            return NotFound();
        }

        if (!await CanEditContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // Set the version comment from the finalize body when it wasn't given at create (don't overwrite one).
        if (!string.IsNullOrWhiteSpace(request?.Comment) && string.IsNullOrEmpty(version.Comment))
        {
            version.Comment = request.Comment.Trim();
        }

        // Storage-quota enforcement (ADR "Per-tenant storage quota"): reject a not-yet-confirmed upload that would
        // push the tenant past its quota, before it's counted. The uploaded blob is best-effort deleted and the
        // pending version row removed, so a rejected upload leaves nothing behind. Skipped on a re-finalize (already
        // Confirmed → already counted).
        if (version.Status == DocumentVersionStatus.Pending)
        {
            var sizeBytes = await _objectStorageClient.GetObjectSizeAsync(version.ObjectKey, cancellationToken);
            if (!await _storageQuota.CanStoreAsync(version.TenantId, sizeBytes, cancellationToken))
            {
                await _objectStorageClient.DeleteObjectAsync(version.ObjectKey, cancellationToken);
                _dbContext.DocumentVersions.Remove(version);
                await _dbContext.SaveChangesAsync(cancellationToken);
                throw new StorageQuotaExceededException("This upload would exceed the tenant's storage quota.");
            }
        }

        // Confirms (server-side hash + version number), auto-classifies, and files email attachments —
        // idempotent, a no-op on an already-Confirmed version (ADR "DocumentVersionsController
        // resource-oriented redesign"). Shared with inbox filing via DocumentFinalizer.
        var wasPending = version.Status == DocumentVersionStatus.Pending;
        await _finalizer.FinalizeAsync(version, cancellationToken);

        var row = new VersionRow(versionId, documentId, version.Status, version.VersionNumber, version.ObjectKey, version.Sha256Hash, version.CreatedAt, version.DocumentDate, version.CreatedByUserId, version.CreatedByServiceAccountId, version.OcrLanguages, version.Comment);

        var documentName = await LoadDocumentNameAsync(documentId, cancellationToken);

        // Audit only the actual confirm (ADR "Audit every-mutation coverage — document lifecycle"); a re-finalize
        // of an already-Confirmed version is a no-op and isn't re-recorded.
        if (wasPending)
        {
            await _audit.RecordAsync(AuditActions.DocumentVersionAdded, "Document", documentId, documentName, $"Version {version.VersionNumber}", cancellationToken: cancellationToken);
        }

        return Ok(await BuildResourceAsync(row, documentName, cancellationToken));
    }

    // Makes an earlier version current (roll back) by setting the document's CurrentVersionId pointer to it —
    // no blob copy, so the pinned version's annotations / document date / everything are preserved (ADR
    // "Version-restore via a current-version pointer", issue #265). Non-destructive: history is untouched, no new
    // version, no extra storage. Gated on CanEditContent; refused while the document is frozen (legal hold),
    // checked out by another, or has an approval workflow in progress. Uploading a new version later clears the
    // pointer back to null (DocumentFinalizer), so the new upload becomes current.
    [HttpPost("{versionId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var source = await _dbContext.DocumentVersions
            .SingleOrDefaultAsync(v => v.Id == versionId && v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed, cancellationToken);
        if (source is null)
        {
            return NotFound(); // no such confirmed version of this document
        }

        if (!await CanEditContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);
        await EnsureNoWorkflowInProgressAsync(documentId, cancellationToken);

        // Idempotent: pinning the already-current version is a no-op that still returns 200.
        if (document.CurrentVersionId != source.Id)
        {
            document.CurrentVersionId = source.Id;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _queue.EnqueueAsync(documentId, cancellationToken);            // the indexed "current" content changed
            await _wormLock.ReconcileAsync(documentId, cancellationToken);        // the retention anchor (current version's date) moved

            var documentName = await LoadDocumentNameAsync(documentId, cancellationToken);
            await _audit.RecordAsync(AuditActions.DocumentVersionRestored, "Document", documentId, documentName,
                $"Made version {source.VersionNumber} current", cancellationToken: cancellationToken);

            // Recorded in the chat thread too (ADR 0545) — this is the one document action that changes what
            // everyone else sees without adding anything, so it deserves to be visible rather than silent. Inside
            // the idempotency branch, so re-pinning the current version stays a no-op. The author is the caller,
            // not whoever uploaded the version originally.
            var (restoredByUserId, restoredByServiceAccountId) = GetCallerIdentity();
            await _chatEntries.RecordVersionActivatedAsync(
                source.TenantId, documentId, source.Id, restoredByUserId, restoredByServiceAccountId, cancellationToken);
        }

        var name = await LoadDocumentNameAsync(documentId, cancellationToken);
        var row = new VersionRow(source.Id, documentId, source.Status, source.VersionNumber, source.ObjectKey, source.Sha256Hash, source.CreatedAt, source.DocumentDate, source.CreatedByUserId, source.CreatedByServiceAccountId, source.OcrLanguages, source.Comment);
        return Ok(await BuildResourceAsync(row, name, cancellationToken));
    }

    // Refuses an operation while the document has an approval workflow in progress — a version In Review or
    // Approved but not yet Released (ADR "Version restore").
    private async Task EnsureNoWorkflowInProgressAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var inProgress = await _dbContext.WorkflowStates
            .Where(w => (w.Status == WorkflowStatus.InReview || w.Status == WorkflowStatus.Approved)
                && _dbContext.DocumentVersions.Any(v => v.Id == w.DocumentVersionId && v.DocumentId == documentId))
            .AnyAsync(cancellationToken);
        if (inProgress)
        {
            throw new WorkflowInProgressException();
        }
    }

    // Edits a version's issuing date. Gated on CanEditIndexData (metadata, like the
    // mask/index-data sub-resources) and enqueues a reindex so search reflects the new date. See ADR
    // "System-field search (creator/dates + document date)".
    [HttpPut("{versionId:guid}/document-date")]
    public async Task<IActionResult> SetDocumentDate(Guid documentId, Guid versionId, [FromBody] SetDocumentDateRequest request, CancellationToken cancellationToken)
    {
        var version = await _dbContext.DocumentVersions.SingleOrDefaultAsync(v => v.Id == versionId && v.DocumentId == documentId, cancellationToken);

        if (version is null)
        {
            return NotFound();
        }

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        if (!DateOnly.TryParse(request.DocumentDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidDocumentDateException($"'{request.DocumentDate}' is not a valid date (expected yyyy-MM-dd).");
        }

        version.DocumentDate = date;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _queue.EnqueueAsync(documentId, cancellationToken);
        await _wormLock.ReconcileAsync(documentId, cancellationToken); // the retention anchor (document date) moved

        var row = new VersionRow(versionId, documentId, version.Status, version.VersionNumber, version.ObjectKey, version.Sha256Hash, version.CreatedAt, version.DocumentDate, version.CreatedByUserId, version.CreatedByServiceAccountId, version.OcrLanguages, version.Comment);
        var documentName = await LoadDocumentNameAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentDateChanged, "Document", documentId, documentName, $"Document date set to {date:yyyy-MM-dd} (version {version.VersionNumber})", cancellationToken: cancellationToken);
        return Ok(await BuildResourceAsync(row, documentName, cancellationToken));
    }

    public class SetDocumentDateRequest
    {
        public string DocumentDate { get; set; } = "";
    }

    private async Task<VersionRow?> LoadForReadAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        // Serve soft-deleted (recycle-bin) documents' versions too (ADR "Recycle bin tab").
        if (!await _dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return null;
        }

        var version = await _dbContext.DocumentVersions
            .Where(v => v.Id == versionId && v.DocumentId == documentId)
            .Select(v => new { v.Status, v.VersionNumber, v.ObjectKey, v.Sha256Hash, v.CreatedAt, v.DocumentDate, v.CreatedByUserId, v.CreatedByServiceAccountId, v.OcrLanguages, v.Comment })
            .SingleOrDefaultAsync(cancellationToken);

        if (version is null)
        {
            return null;
        }

        return new VersionRow(versionId, documentId, version.Status, version.VersionNumber, version.ObjectKey, version.Sha256Hash, version.CreatedAt, version.DocumentDate, version.CreatedByUserId, version.CreatedByServiceAccountId, version.OcrLanguages, version.Comment);
    }

    // The document's Name — used as the download filename (never the opaque object key). Loaded once per
    // request and shared across a document's versions. Was previously the "Short Description" index field,
    // dropped as a duplicate of Document.Name (ADR "Drop redundant Short Description / Doc Date mask
    // fields", superseding ADR "Download filename from Short Description") — a document is named after its
    // file, so Name already is the full filename, and using it directly also stays correct after a rename.
    private async Task<string> LoadDocumentNameAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.Name)
            .SingleAsync(cancellationToken);
    }

    private async Task<DocumentVersionResource> BuildResourceAsync(VersionRow version, string documentName, CancellationToken cancellationToken)
    {
        var links = new List<Link> { new("self", $"/api/documents/{version.DocumentId}/versions/{version.Id}", "GET") };
        var previewConverted = false;

        if (version.Status == DocumentVersionStatus.Confirmed)
        {
            // Name the download after Document.Name but with the *version object's* extension, so a version
            // whose type differs from the document name saves correctly — e.g. the searchable-PDF successor of
            // a `.tif` document downloads as `<name>.pdf` (ADR "Searchable PDF successor for TIFFs"), and an
            // email named after its subject (no extension) saves as `<subject>.eml` (ADR "Email
            // auto-classification"). Falls back to the raw name when the object key carries no extension.
            var objectExtension = Path.GetExtension(version.ObjectKey);
            var downloadFileName = string.IsNullOrEmpty(objectExtension)
                ? documentName
                : Path.GetFileNameWithoutExtension(documentName) + objectExtension;

            var downloadUrl = await _objectStorageClient.GetPresignedDownloadUrlAsync(version.ObjectKey, PresignedUrlExpiry, downloadFileName, cancellationToken);
            links.Add(new Link("download", downloadUrl.ToString(), "GET"));

            // Inline-disposition URL the workbench preview renders in place — see ADR "Repositories
            // workbench UI". For formats the browser can't display (TIFF, office docs), this resolves to a
            // cached rendition instead of the original (ADR "Server-side preview renditions", "Office
            // document preview via Gotenberg"). Null when no viewable preview can be produced (conversion
            // failed / converter down) — omit the link so the client shows "No preview available" rather
            // than a blank pane (ADR "Preview fallback when a rendition can't be produced").
            var preview = await _documentPreviewService.GetPreviewUrlAsync(version.ObjectKey, PresignedUrlExpiry, downloadFileName, cancellationToken);
            if (preview is not null)
            {
                links.Add(new Link("preview", preview.Url.ToString(), "GET"));
                previewConverted = preview.IsConverted;
            }

            // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"). A static link — the
            // endpoint computes/caches the layout on demand and returns 204 for formats with no overlay — so
            // building the resource stays cheap (no OCR/PDF parse here).
            links.Add(new Link("text-layout", $"/api/documents/{version.DocumentId}/versions/{version.Id}/text-layout", "GET"));

            // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages"). Static
            // link; the endpoint returns 204 for every other format (the client then uses the single `preview`).
            links.Add(new Link("preview-pages", $"/api/documents/{version.DocumentId}/versions/{version.Id}/preview-pages", "GET"));

            // The version's approval workflow (ADR "Workflow / document state model", 0009). Static link — the
            // endpoint resolves the current status + valid-transition links on demand.
            links.Add(new Link("workflow", $"/api/documents/{version.DocumentId}/versions/{version.Id}/workflow", "GET"));

            // Roll back to this version (ADR "Version restore") — copies its content into a new current version.
            // Static link (the action enforces CanEditContent + the frozen/checked-out/workflow guards); the
            // client offers it for older versions.
            links.Add(new Link("restore", $"/api/documents/{version.DocumentId}/versions/{version.Id}/restore", "POST"));

            // Sticky notes / positional annotations pinned to this version's pages (ADR "Document annotations
            // (sticky notes)"). Static link — the endpoint lists them on demand.
            links.Add(new Link("annotations", $"/api/documents/{version.DocumentId}/versions/{version.Id}/annotations", "GET"));

            // This version's issuing date (ADR "System-field search"). Static like `restore` above — the PUT
            // enforces CanEditIndexData plus the frozen/checked-out guards, and resolving all of that per row
            // would put a rights + legal-hold + checkout lookup on every version in the list.
            links.Add(new Link("document-date", $"/api/documents/{version.DocumentId}/versions/{version.Id}/document-date", "PUT"));
        }

        return new DocumentVersionResource
        {
            Id = version.Id,
            VersionNumber = version.VersionNumber,
            ObjectKey = version.ObjectKey,
            Sha256Hash = version.Sha256Hash,
            Status = version.Status.ToString(),
            PreviewConverted = previewConverted,
            CreatedAt = version.CreatedAt,
            CreatedByName = await ResolveCreatorNameAsync(version.CreatedByUserId, version.CreatedByServiceAccountId, cancellationToken),
            DocumentDate = version.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            OcrLanguages = version.OcrLanguages,
            FileExtension = Path.GetExtension(version.ObjectKey),
            Comment = version.Comment,
            Links = links,
        };
    }

    // The display name of a version's creator (exactly one of the two ids is set — see the CreatedBy CHECK).
    private async Task<string> ResolveCreatorNameAsync(Guid? userId, Guid? serviceAccountId, CancellationToken cancellationToken)
    {
        if (userId is { } uid)
        {
            return await _dbContext.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "";
        }

        if (serviceAccountId is { } said)
        {
            return await _dbContext.ServiceAccounts.Where(s => s.Id == said).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken) ?? "";
        }

        return "";
    }

    // Checks ServiceAccount first, then a logged-in User — the two accessors are mutually exclusive per
    // request (CurrentPrincipalMiddleware's three-way branch). See ADR "Document-scope authorization
    // retrofit for User, and tenant-administrator-driven onboarding".
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

    private async Task<bool> CanEditContentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent;
    }

    private async Task<bool> CanReadContentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanReadContent;
    }

    // Content access for a specific version (ADR "Workflow status-gating"): requires CanReadContent, and — if
    // the version is "gated" (it entered a workflow and isn't yet Released) — also CanEditContent (editors /
    // tenant admins) or being that version's assigned reviewer. A never-submitted version (no WorkflowState)
    // and a Released version are ungated, so only in-workflow, not-yet-Released versions are restricted.
    private async Task<bool> CanAccessVersionContentAsync(Guid versionId, Guid documentId, CancellationToken cancellationToken)
    {
        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanReadContent)
        {
            return false;
        }

        if (rights.CanEditContent)
        {
            return true; // editors / admins see every version
        }

        var state = await _dbContext.WorkflowStates.FirstOrDefaultAsync(w => w.DocumentVersionId == versionId, cancellationToken);
        if (state is null || state.Status == WorkflowStatus.Released)
        {
            return true; // ungated
        }

        return _currentUserAccessor.UserId is { } userId && state.AssignedToUserId == userId; // the assigned reviewer
    }

    private async Task<bool> CanEditIndexDataAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanEditIndexData;
    }

    // Returns whichever principal actually made this request, for DocumentVersion creator attribution
    // (CreatedByUserId/CreatedByServiceAccountId, CHECK-constrained to exactly one).
    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity()
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (null, serviceAccountId);
        }

        return (_currentUserAccessor.UserId, null);
    }
}
