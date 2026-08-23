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
/// The document's editable metadata: mask assignment, index data, sensitivity label, OCR languages, and a
/// folder's contents-sort-order. Split out of DocumentsController (#466); routes unchanged. Each write is
/// guarded by the same frozen/checked-out checks the monolith applied (ADR "Legal hold enforcement").
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class DocumentMetadataController : ControllerBase
{
    private readonly IWormLockService _wormLock;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly Documents.DocumentAccessService _access;
    private readonly IAuditRecorder _audit;
    private readonly IDocumentIndexQueue _queue;
    private readonly ISearchablePdfQueue _searchablePdfQueue;
    private readonly IObjectStorageClient _objectStorage;
    private readonly Documents.MailboxAddressClaims _mailboxAddressClaims;

    public DocumentMetadataController(
        IWormLockService wormLock,
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        IAuditRecorder audit,
        IDocumentIndexQueue queue,
        ISearchablePdfQueue searchablePdfQueue,
        IObjectStorageClient objectStorage,
        Documents.MailboxAddressClaims mailboxAddressClaims)
    {
        _wormLock = wormLock;
        _dbContext = dbContext;
        _access = access;
        _audit = audit;
        _queue = queue;
        _searchablePdfQueue = searchablePdfQueue;
        _objectStorage = objectStorage;
        _mailboxAddressClaims = mailboxAddressClaims;
    }

    // Plain mutable class, not a record — same XmlSerializer rationale as elsewhere.
    public class MaskAssignmentResource : HypermediaResource
    {
        public Guid? MaskId { get; set; }

        public Guid? MaskVersionId { get; set; }

        public string? Name { get; set; }

        public int? VersionNumber { get; set; }
    }

    public class SetMaskRequest
    {
        public Guid MaskId { get; set; }
    }

    // A dedicated sub-resource, not folded into the rename PUT above — that endpoint's contract is
    // deliberately narrow ("owns only Name," ADR "DocumentVersionsController resource-oriented
    // redesign"). Always resolves to the mask's *current* MaskVersion — mirrors how RepositoryMask used
    // to auto-pick the newest version, now resolved directly since there's no assignment table anymore
    // (ADR "Repository/Document unification"). See ADR "Document metadata (index data) endpoints".
    [HttpGet("mask")]
    public async Task<IActionResult> GetMask(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"]) // serve recycle-bin items (ADR "Recycle bin tab")
            .Where(d => d.Id == documentId)
            .Select(d => new { d.MaskVersionId })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return Ok(await BuildMaskResourceAsync(documentId, document.MaskVersionId, cancellationToken));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("mask")]
    public async Task<IActionResult> HeadMask(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpPut("mask")]
    public async Task<IActionResult> SetMask(Guid documentId, [FromBody] SetMaskRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        var mask = await _dbContext.MaskVersions
            .Where(v => v.MaskId == request.MaskId && v.IsCurrent)
            .Select(v => new { v.Id, v.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (mask is null)
        {
            throw new MaskNotFoundException();
        }

        document.MaskVersionId = mask.Id;

        try
        {
            // Translating save (#562/#564, and now ADR 0672): the refusals SaveChanges raises for containment,
            // personal-space structure and an immutable folder type must NOT reach the catch below, which
            // reports every InvalidOperationException as a missing required field.
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Fires when the newly-assigned mask has a Required field with no value yet (ADR "Required
            // field validation trigger") — the intended flow is filling in index data first via PUT
            // .../index-data, then assigning the mask last.
            throw new RequiredFieldMissingException(ex.Message);
        }

        await _queue.EnqueueAsync(documentId, cancellationToken);
        await _wormLock.ReconcileAsync(documentId, cancellationToken); // the mask's retention may now apply
        await _audit.RecordAsync(AuditActions.DocumentMaskAssigned, "Document", documentId, document.Name, $"Mask set to '{mask.Name}'", cancellationToken: cancellationToken);

        return Ok(await BuildMaskResourceAsync(documentId, mask.Id, cancellationToken));
    }

    public class SetContentsSortOrderRequest
    {
        // The persisted default contents sort order for this folder (ADR "Per-folder contents sort order"):
        // Name=0 / DocumentDate=1 / Created=2.
        public FolderContentsSortOrder SortOrder { get; set; }
    }

    public class ContentsSortOrderResource : HypermediaResource
    {
        public FolderContentsSortOrder ContentsSortOrder { get; set; }
    }

    // Sets a folder's persisted default contents sort order (ADR "Per-folder contents sort order") — a shared,
    // per-folder setting (it changes the default order for everyone who opens the folder). A metadata edit:
    // CanEditIndexData. Applied client-side (folders-first) when the folder is opened; a column-header click is
    // an ephemeral override. An undefined enum value → 400 INVALID_CONTENTS_SORT_ORDER. No re-index (ordering is
    // client-side, it doesn't affect the search index).
    [HttpPut("contents-sort-order")]
    public async Task<IActionResult> SetContentsSortOrder(Guid documentId, [FromBody] SetContentsSortOrderRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (!Enum.IsDefined(request.SortOrder))
        {
            throw new InvalidContentsSortOrderException();
        }

        document.ContentsSortOrder = request.SortOrder;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.DocumentContentsSortOrderChanged, "Document", documentId, document.Name, $"Contents sort order set to {request.SortOrder}", cancellationToken: cancellationToken);

        return Ok(new ContentsSortOrderResource
        {
            ContentsSortOrder = document.ContentsSortOrder,
            Links = [new Link("self", $"/api/documents/{documentId}/contents-sort-order", "GET")],
        });
    }

    public class SetSensitivityRequest
    {
        // The per-tenant sensitivity label id, or null to clear to None (ADR "Configurable sensitivity labels").
        public Guid? LabelId { get; set; }
    }

    public class SensitivityResource : HypermediaResource
    {
        public Guid? SensitivityLabelId { get; set; }
        public string SensitivityLabelName { get; set; } = "";
    }

    // Sets the document's data-classification / sensitivity label (ADR "Configurable sensitivity labels + upload
    // defaults"). A metadata edit — CanEditIndexData, refused while frozen (legal hold) or checked out by another;
    // audited + re-indexed (so the search filter reflects it). A null LabelId clears to None; an unknown / retired
    // label id → 400 INVALID_SENSITIVITY_LABEL.
    [HttpPut("sensitivity")]
    public async Task<IActionResult> SetSensitivity(Guid documentId, [FromBody] SetSensitivityRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        string? labelName = null;
        if (request.LabelId is { } labelId)
        {
            labelName = await _dbContext.SensitivityLabelDefinitions
                .Where(l => l.Id == labelId && l.RetiredAt == null)
                .Select(l => l.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (labelName is null)
            {
                throw new InvalidSensitivityLabelException();
            }
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        document.SensitivityLabelId = request.LabelId;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _queue.EnqueueAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentSensitivityChanged, "Document", documentId, document.Name, $"Sensitivity set to {labelName ?? "None"}", cancellationToken: cancellationToken);

        return Ok(new SensitivityResource { SensitivityLabelId = request.LabelId, SensitivityLabelName = labelName ?? "", Links = [new Link("self", $"/api/documents/{documentId}/sensitivity", "GET")] });
    }

    [HttpDelete("mask")]
    public async Task<IActionResult> ClearMask(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        document.MaskVersionId = null;

        // Clearing is a change: an untyped Mailbox breaks the projection exactly as a re-typed one does
        // (ADR 0672), so this path is refused for the same folders and needs the same translation.
        await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);

        await _queue.EnqueueAsync(documentId, cancellationToken);
        await _wormLock.ReconcileAsync(documentId, cancellationToken); // retention no longer applies
        await _audit.RecordAsync(AuditActions.DocumentMaskCleared, "Document", documentId, document.Name, cancellationToken: cancellationToken);

        return NoContent();
    }

    // Ordered OCR-language codes (first = highest priority) — the system-field picker's selection (ADR
    // "Per-tenant / per-version OCR languages"). Empty clears the override (inherit the tenant default).
    public class SetOcrLanguagesRequest
    {
        public List<string> Languages { get; set; } = [];
    }

    // Sets the OCR-language override on the document's latest TIFF source version and re-runs the searchable-PDF
    // conversion with it (ADR "Per-tenant / per-version OCR languages"). Only meaningful for a TIFF-sourced
    // document. Requires CanEditIndexData (a per-version metadata edit, like the document-date system field).
    [HttpPut("ocr-languages")]
    public async Task<IActionResult> SetOcrLanguages(Guid documentId, [FromBody] SetOcrLanguagesRequest request, CancellationToken cancellationToken)
    {
        var documentName = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken);
        if (documentName is null)
        {
            return NotFound();
        }

        if (!await _access.CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        // Validate every code against the fixed catalog, preserving the caller's order (Tesseract priority).
        var codes = request.Languages.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        var known = OcrLanguages.Supported.Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        var unknown = codes.FirstOrDefault(c => !known.Contains(c));
        if (unknown is not null)
        {
            throw UnknownOcrLanguageException.Unsupported(unknown);
        }

        // The conversion source: the latest confirmed TIFF version.
        var tiffVersion = await _dbContext.DocumentVersions
            .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(v => v.ObjectKey.ToLower().EndsWith(".tif") || v.ObjectKey.ToLower().EndsWith(".tiff"), cancellationToken);

        if (tiffVersion is null)
        {
            throw new NoTiffVersionException();
        }

        tiffVersion.OcrLanguages = codes.Count == 0 ? null : string.Join('+', codes);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Re-run the conversion with the new languages → a new searchable-PDF version (no-op when the OCR
        // sidecar isn't configured).
        await _searchablePdfQueue.EnqueueAsync(documentId, tiffVersion.Id, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentOcrLanguagesChanged, "Document", documentId, documentName,
            codes.Count == 0 ? "OCR languages reset to tenant default" : $"OCR languages set to {string.Join('+', codes)}", cancellationToken: cancellationToken);

        return Ok(new OcrLanguagesResource
        {
            Languages = codes,
            Links = [new Link("self", $"/api/documents/{documentId}/ocr-languages", "GET")],
        });
    }

    public class OcrLanguagesResource : HypermediaResource
    {
        public List<string> Languages { get; set; } = [];
    }

    private async Task<MaskAssignmentResource> BuildMaskResourceAsync(Guid documentId, Guid? maskVersionId, CancellationToken cancellationToken)
    {
        var resource = new MaskAssignmentResource
        {
            Links = [new Link("self", $"/api/documents/{documentId}/mask", "GET")],
        };

        if (maskVersionId is not { } id)
        {
            return resource;
        }

        var version = await _dbContext.MaskVersions
            .Where(v => v.Id == id)
            .Select(v => new { v.MaskId, v.Name, v.VersionNumber })
            .SingleAsync(cancellationToken);

        resource.MaskId = version.MaskId;
        resource.MaskVersionId = id;
        resource.Name = version.Name;
        resource.VersionNumber = version.VersionNumber;

        return resource;
    }

    public class FieldValueGroup
    {
        public Guid FieldDefinitionId { get; set; }

        public string FieldName { get; set; } = "";

        public List<string> Values { get; set; } = [];
    }

    public class IndexDataResource : HypermediaResource
    {
        public List<FieldValueGroup> Fields { get; set; } = [];
    }

    // Named "index-data", not "fields" — matches the existing AclEntry.CanEditIndexData right and the
    // "index fields" vocabulary (ADR "Metadata / index-field model"). Only fields that actually have a
    // value are included — this is the EAV data itself, not the mask's schema (GET /masks/{id} already
    // covers "what fields does this mask define"). See ADR "Document metadata (index data) endpoints".
    [HttpGet("index-data")]
    public async Task<IActionResult> GetIndexData(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return Ok(await BuildIndexDataResourceAsync(documentId, cancellationToken));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("index-data")]
    public async Task<IActionResult> HeadIndexData(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    public class SetFieldValueGroup
    {
        public Guid FieldDefinitionId { get; set; }

        public List<string> Values { get; set; } = [];
    }

    public class SetIndexDataRequest
    {
        public List<SetFieldValueGroup> Fields { get; set; } = [];

        // Confirms a duplicate mailbox-address claim (#703): the first attempt answers 409
        // DUPLICATE_ADDRESS_CLAIM naming the other mailbox, and the retry carries true to make delivery fan
        // out to both. Meaningless (and ignored) on every other field.
        public bool ConfirmDuplicateClaims { get; set; }
    }

    // Replaces the entire FieldValue set for the document in one request — matches PUT's "here is what
    // this resource should now be" contract and how a metadata form is naturally submitted/edited as a
    // whole, not per-field. No per-field granular endpoints. See ADR "Document metadata (index data)
    // endpoints".
    [HttpPut("index-data")]
    public async Task<IActionResult> SetIndexData(Guid documentId, [FromBody] SetIndexDataRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId, d.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await _access.CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        var fieldDefinitionIds = request.Fields.Select(f => f.FieldDefinitionId).ToList();
        var fieldDefinitions = await _dbContext.FieldDefinitions
            .Where(f => fieldDefinitionIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);

        foreach (var field in request.Fields)
        {
            if (!fieldDefinitions.TryGetValue(field.FieldDefinitionId, out var definition))
            {
                throw new FieldDefinitionNotFoundException($"Field definition '{field.FieldDefinitionId}' does not exist.");
            }

            // New validation this endpoint introduces — never had to be enforced before, since FieldValue
            // rows only ever came from direct DbContext seeding in tests/verification scripts.
            //
            // Multiplicity now comes from EITHER the flag or the type (#703): `IsList` says so for any basic
            // type, and MultiSelect is a list by virtue of being one — grandfathered, so an existing
            // MultiSelect field keeps accepting many values without anybody having to set the flag on it.
            if (!definition.IsList && definition.DataType != FieldDataType.MultiSelect && field.Values.Count > 1)
            {
                throw new MultipleValuesNotAllowedException($"Field '{definition.Name}' does not allow multiple values.");
            }
        }

        // The mail-routing rules (#703): who may write a Mailbox's address list, and which claims it may
        // carry. Before the rewrite below, because it compares the request against the STORED list.
        await _mailboxAddressClaims.EnforceAsync(documentId, document.Name, fieldDefinitions, request.Fields, request.ConfirmDuplicateClaims, cancellationToken);

        var existingValues = await _dbContext.FieldValues.Where(v => v.DocumentId == documentId).ToListAsync(cancellationToken);
        _dbContext.FieldValues.RemoveRange(existingValues);

        foreach (var field in request.Fields)
        {
            // Stamped in the order the caller sent them (#703) — a list is what the user typed, so its order
            // is theirs. Without it the read came back in whatever order the database chose, and that order
            // changed between reads.
            for (var ordinal = 0; ordinal < field.Values.Count; ordinal++)
            {
                _dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = document.TenantId,
                    DocumentId = documentId,
                    FieldDefinitionId = field.FieldDefinitionId,
                    Value = field.Values[ordinal],
                    Ordinal = ordinal,
                });
            }
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Fires from the Format/Range checks (ADR "Format/Range/Required validation enforcement
            // mechanism") — e.g. a value that doesn't match its field's FormatPattern or falls outside
            // MinValue/MaxValue.
            throw new FieldValueInvalidException(ex.Message);
        }

        await _queue.EnqueueAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentIndexDataUpdated, "Document", documentId, document.Name, "Index data updated", cancellationToken: cancellationToken);

        return Ok(await BuildIndexDataResourceAsync(documentId, cancellationToken));
    }

    private async Task<IndexDataResource> BuildIndexDataResourceAsync(Guid documentId, CancellationToken cancellationToken)
    {
        // Ordered by Ordinal, tie-broken on Id (#703): the tie-break is what gives a STABLE order to rows
        // written before ordinals existed, which all share 0 — arbitrary, but no longer different each read.
        var rows = await _dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Id, f.Name, v.Value, v.Ordinal, ValueId = v.Id })
            .OrderBy(r => r.Ordinal).ThenBy(r => r.ValueId)
            .ToListAsync(cancellationToken);

        var fields = rows
            .GroupBy(r => new { r.Id, r.Name })
            .Select(g => new FieldValueGroup
            {
                FieldDefinitionId = g.Key.Id,
                FieldName = g.Key.Name,
                Values = g.Select(r => r.Value).ToList(),
            })
            .ToList();

        return new IndexDataResource
        {
            Fields = fields,
            Links = [new Link("self", $"/api/documents/{documentId}/index-data", "GET")],
        };
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
