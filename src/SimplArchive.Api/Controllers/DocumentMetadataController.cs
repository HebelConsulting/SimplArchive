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
    private readonly Documents.IndexDataWriter _indexData;
    private readonly Documents.OcrLanguageWriter _ocrLanguages;

    public DocumentMetadataController(
        IWormLockService wormLock,
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        IAuditRecorder audit,
        IDocumentIndexQueue queue,
        ISearchablePdfQueue searchablePdfQueue,
        IObjectStorageClient objectStorage,
        Documents.MailboxAddressClaims mailboxAddressClaims,
        Documents.IndexDataWriter indexData,
        Documents.OcrLanguageWriter ocrLanguages)
    {
        _wormLock = wormLock;
        _dbContext = dbContext;
        _access = access;
        _audit = audit;
        _queue = queue;
        _searchablePdfQueue = searchablePdfQueue;
        _objectStorage = objectStorage;
        _mailboxAddressClaims = mailboxAddressClaims;
        _indexData = indexData;
        _ocrLanguages = ocrLanguages;
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

        // Which version is current, and the refusal when there is none, is MaskAssignment's — shared with
        // ADR 0794's combined PUT .../detail. The assignment itself stays here: it is one line.
        var mask = await Documents.MaskAssignment.ResolveCurrentVersionAsync(_dbContext, request.MaskId, cancellationToken);

        document.MaskVersionId = mask.VersionId;

        HonourIfMatch(document);

        try
        {
            // Translating save (#562/#564, and now ADR 0672): the refusals SaveChanges raises for containment,
            // personal-space structure and an immutable folder type must NOT reach the catch below, which
            // reports every InvalidOperationException as a missing required field.
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        // BEFORE the InvalidOperationException catch: a stale token must not be reported as a missing required
        // field — an error naming a cause that never happened is worse than none.
        catch (DbUpdateConcurrencyException)
        {
            throw Errors.Exceptions.Concurrency.EtagMismatchException.ForDocument();
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

        return Ok(await BuildMaskResourceAsync(documentId, mask.VersionId, cancellationToken));
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
        HonourIfMatch(document);
        await SaveHonouringEtagAsync(cancellationToken);

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
        public string SensitivityLabelName { get; set; } = string.Empty;
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
        HonourIfMatch(document);
        await SaveHonouringEtagAsync(cancellationToken);

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
        HonourIfMatch(document);

        // Clearing is a change: an untyped Mailbox breaks the projection exactly as a re-typed one does
        // (ADR 0672), so this path is refused for the same folders and needs the same translation.
        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Errors.Exceptions.Concurrency.EtagMismatchException.ForDocument();
        }

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

        // Validation, the source-version choice and the assignment belong to OcrLanguageWriter, which ADR
        // 0794's combined PUT .../detail calls too. It hands the version back so the re-conversion is
        // enqueued AFTER the commit, by whoever owns it.
        var codes = request.Languages.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        var sourceVersion = await _ocrLanguages.ApplyAsync(documentId, request.Languages, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Re-run the conversion with the new languages → a new searchable-PDF version (no-op when the OCR
        // sidecar isn't configured; a PDF the detector calls NotAScan stays unconverted here — the forced
        // path is the make-searchable rel, a deliberate act).
        await _searchablePdfQueue.EnqueueAsync(documentId, sourceVersion.Id, cancellationToken: cancellationToken);
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
        // The token the caller sends back as If-Match (#1083). Emitted HERE because every read and every
        // write's response funnels through this builder — put on the individual returns, it would be the
        // sixth site somebody forgets. SetETag was dead code until now.
        if (await _dbContext.Documents.Where(d => d.Id == documentId)
                .Select(d => (Guid?)d.ConcurrencyToken).SingleOrDefaultAsync(cancellationToken) is { } token)
        {
            SetETag(token);
        }

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

        // The mask DEFINITION — where this assignment's field definitions live, and therefore what an index
        // editor needs before it can offer a single box (#729, ADR 0688).
        //
        // Advertised here because this is the only place that knows which mask a document wears. Both clients
        // used to answer it by looking the id up in the mask CATALOGUE and following that row's `self`, which
        // works for every mask the catalogue carries — and the catalogue is filtered to the freely-assignable
        // ones (#671), so a typed folder (Mailbox, Calendar, Addressbook, a repository) had no row, no address,
        // and its editor opened with no fields at all. A missing rel means "not available" (ADR 0543); the
        // absence here meant "not addressable", which is a different thing and was never true.
        resource.Links.Add(new Link("definition", $"/api/masks/{version.MaskId}", "GET"));

        return resource;
    }

    public class FieldValueGroup
    {
        public Guid FieldDefinitionId { get; set; }

        public string FieldName { get; set; } = string.Empty;

        /// <summary>The field's declared type — what lets a READ view render a DateTime value as a local
        /// wall clock instead of the raw ISO-with-offset wire string (the ADR 0744-era pane fix).</summary>
        public string DataType { get; set; } = string.Empty;

        public List<string> Values { get; set; } = [];

        /// <summary>
        /// For a <c>DocumentReference</c> field only: the targets its <see cref="Values"/> name, resolved
        /// server-side and index-aligned with them. Empty for every other type.
        /// </summary>
        /// <remarks>
        /// Resolved HERE rather than by the client, because the client resolving each id would be one request
        /// per value on every detail open — the per-rel round trip ADR 0557 forbids — and it would have to
        /// compose the address from an id, which ADR 0543 forbids outright.
        /// </remarks>
        public List<DocumentReferenceTarget> Targets { get; set; } = [];
    }

    /// <summary>One resolved target of a <c>DocumentReference</c> value.</summary>
    /// <remarks>
    /// A target the caller may not open carries NO name and NO link — only the id it already holds, since the
    /// value is in a field they can read. The name is what would leak: a person's name, a case number, is
    /// usually the sensitive part of the document the ACL is protecting.
    ///
    /// Unavailable is ONE state on purpose. "You may not see it" and "it no longer exists" are not
    /// distinguished, because telling them apart tells a caller that a document they cannot see exists — and
    /// neither answer changes what they can do about it. The cost is real and accepted: a value whose target
    /// was purged looks, to someone without rights on it, like a value they simply cannot follow. Anyone who
    /// CAN see the target sees it resolve normally, so a genuinely dangling value is visible to exactly the
    /// people positioned to fix it.
    /// </remarks>
    public class DocumentReferenceTarget
    {
        public Guid Id { get; set; }

        /// <summary>The target's name, or null when the caller may not open it.</summary>
        public string? Name { get; set; }

        /// <summary>The <c>document</c> rel, or empty when the caller may not open it — absence means
        /// "not available to you, here, now" (ADR 0543), which is exactly true in both unavailable cases.</summary>
        public List<Link> Links { get; set; } = [];
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
        // The ENTITY, not a projection — this endpoint has to be able to bump the document's token (#1083).
        var document = await _dbContext.Documents
            .SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

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

        // The whole replacement — validation, the mail-routing claims, the classifier-owned guard, and the
        // rewrite of the rows — belongs to IndexDataWriter, which ADR 0794's combined PUT .../detail calls
        // too. What stays here is the envelope: who may write, the precondition, the save, the side effects.
        await _indexData.ApplyAsync(document, request.Fields, request.ConfirmDuplicateClaims, cancellationToken);

        HonourIfMatch(document);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        // BEFORE the InvalidOperationException catch: a stale token must not be reported as an invalid field
        // value — an error naming a cause that never happened.
        catch (DbUpdateConcurrencyException)
        {
            throw Errors.Exceptions.Concurrency.EtagMismatchException.ForDocument();
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
        // The token the caller sends back as If-Match (#1083). Emitted HERE because every read and every
        // write's response funnels through this builder — put on the individual returns, it would be the
        // sixth site somebody forgets. SetETag was dead code until now.
        if (await _dbContext.Documents.Where(d => d.Id == documentId)
                .Select(d => (Guid?)d.ConcurrencyToken).SingleOrDefaultAsync(cancellationToken) is { } token)
        {
            SetETag(token);
        }

        // FIELD position first (ADR 0761 — the pane shows fields in the mask's display order), then Ordinal
        // within a list field, tie-broken on Id (#703): the tie-break is what gives a STABLE order to rows
        // written before ordinals existed, which all share 0 — arbitrary, but no longer different each read.
        var rows = await _dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Id, f.Name, f.DataType, f.SortOrder, v.Value, v.Ordinal, ValueId = v.Id })
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Ordinal).ThenBy(r => r.ValueId)
            .ToListAsync(cancellationToken);

        var fields = rows
            .GroupBy(r => new { r.Id, r.Name, r.DataType })
            .Select(g => new FieldValueGroup
            {
                FieldDefinitionId = g.Key.Id,
                FieldName = g.Key.Name,
                DataType = g.Key.DataType.ToString(),
                Values = g.Select(r => r.Value).ToList(),
            })
            .ToList();

        await ResolveDocumentReferencesAsync(fields, cancellationToken);

        return new IndexDataResource
        {
            Fields = fields,
            Links = [new Link("self", $"/api/documents/{documentId}/index-data", "GET")],
        };
    }

    /// <summary>Fills in <see cref="FieldValueGroup.Targets"/> for every DocumentReference field.</summary>
    /// <remarks>
    /// Two batched queries for the whole resource, not two per value: the names in one read, the rights in
    /// one <c>GetCallerRightsForManyAsync</c> — which prices a page at roughly what a single document used to
    /// (#858), and is what makes resolving-on-read affordable at all.
    /// </remarks>
    private async Task ResolveDocumentReferencesAsync(List<FieldValueGroup> fields, CancellationToken cancellationToken)
    {
        var groups = fields.Where(f => f.DataType == nameof(FieldDataType.DocumentReference)).ToList();
        if (groups.Count == 0)
        {
            return;
        }

        var ids = groups.SelectMany(g => g.Values)
            .Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        // The tenant and soft-delete filters do the first half of the work: a target that was purged, or is
        // in the recycle bin, simply does not come back and therefore resolves to unavailable.
        var names = await _dbContext.Documents.Where(d => ids.Contains(d.Id))
            .Select(d => new { d.Id, d.Name }).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        var rights = await _access.GetCallerRightsForManyAsync(names.Keys.ToList(), cancellationToken);

        foreach (var group in groups)
        {
            group.Targets = group.Values.Select(value =>
            {
                var target = new DocumentReferenceTarget();
                if (!Guid.TryParse(value, out var id))
                {
                    return target;   // not an id at all — shape is enforced on write, but a read must not throw
                }

                target.Id = id;
                if (names.TryGetValue(id, out var name) && rights.TryGetValue(id, out var right) && right.CanSee)
                {
                    target.Name = name;
                    target.Links = [new Link("document", $"/api/documents/{id}", "GET")];
                }

                return target;
            }).ToList();
        }
    }

    private void SetETag(Guid concurrencyToken)
    {
        Response.Headers.ETag = $"\"{concurrencyToken}\"";
    }

    private static bool TryParseETag(string headerValue, out Guid token)
    {
        return Guid.TryParse(headerValue.Trim('"'), out token);
    }

    /// <summary>
    /// Honours the caller's <c>If-Match</c> when it sent one (#1083): EF then compares that value to the stored
    /// column and raises <see cref="DbUpdateConcurrencyException"/> — rendered as 412 — if somebody else wrote
    /// in between. A no-op when no header was sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This controller had <see cref="SetETag"/> and <see cref="TryParseETag"/> written and called from NOWHERE:
    /// the whole apparatus existed as dead code while six mutations of a TRACKED entity — including
    /// <c>PUT index-data</c>, the one two people editing the same document actually collide on — emitted no tag
    /// and checked no precondition. A tracked entity whose endpoints never check the token is no better than an
    /// untracked one.
    /// </para>
    /// <para>
    /// HONOURED, not required. The web client calls these four addresses with a plain <c>PutAsJsonAsync</c> and
    /// sends no header at all, so demanding one would answer 428 to every save today. Requiring it is a later
    /// step, once both clients send it.
    /// </para>
    /// </remarks>
    private void HonourIfMatch(Domain.Documents.Document document)
    {
        if (Request.Headers.TryGetValue("If-Match", out var values) && TryParseETag(values.ToString(), out var token))
        {
            _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = token;
        }
    }


    /// <summary>SaveChanges, rendering a stale <c>If-Match</c> as 412 rather than a raw 500 (#1083).</summary>
    private async Task SaveHonouringEtagAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Errors.Exceptions.Concurrency.EtagMismatchException.ForDocument();
        }
    }

}
