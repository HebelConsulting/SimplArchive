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
/// See ADR "ETag / If-Match optimistic concurrency", ADR "Nested Document creation", ADR "Document
/// delete/restore (recycle bin) implementation". GET/HEAD (read), PUT (rename), children (list/create),
/// DELETE (soft delete, cascades to descendants, requires If-Match) and POST restore (undoes a DELETE,
/// reparenting to a "Recovered Items" folder if the original parent is gone) so far. Authorization checks
/// are Document-scope, and accept either a ServiceAccount or a logged-in User caller (see ADR
/// "Document-scope authorization retrofit for User, and tenant-administrator-driven onboarding") —
/// ListChildren/CreateChild check against the parent document itself, since that's the container being
/// read/written.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IDocumentIndexQueue _queue;
    private readonly ISearchablePdfQueue _searchablePdfQueue;

    public DocumentsController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IDocumentIndexQueue queue,
        ISearchablePdfQueue searchablePdfQueue,
        IAuditRecorder audit,
        ILegalHoldService legalHold,
        IUserSystemRightsResolver userSystemRights,
        Documents.DocumentPurger purger,
        Documents.DocumentRestorer restorer,
        Documents.RepositoryExporter exporter,
        Documents.RepositoryImporter importer,
        IObjectStorageClient objectStorage,
        Documents.IClearanceScopeResolver clearanceScope,
        IWormLockService wormLock)
    {
        _dbContext = dbContext;
        _clearanceScope = clearanceScope;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _queue = queue;
        _searchablePdfQueue = searchablePdfQueue;
        _audit = audit;
        _legalHold = legalHold;
        _userSystemRights = userSystemRights;
        _purger = purger;
        _restorer = restorer;
        _exporter = exporter;
        _importer = importer;
        _objectStorage = objectStorage;
        _wormLock = wormLock;
    }

    private readonly IWormLockService _wormLock;
    private readonly Documents.IClearanceScopeResolver _clearanceScope;

    private readonly IAuditRecorder _audit;
    private readonly ILegalHoldService _legalHold;
    private readonly IObjectStorageClient _objectStorage;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly Documents.DocumentPurger _purger;
    private readonly Documents.DocumentRestorer _restorer;
    private readonly Documents.RepositoryExporter _exporter;
    private readonly Documents.RepositoryImporter _importer;

    // Permanent destruction is tenant-admin-only (a User right; a ServiceAccount has no IsTenantAdmin) — a
    // stricter bar than the CanDelete that soft-deletes to the recycle bin. See ADR "Manual hard-delete / purge".
    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;
        }

        return false;
    }

    // The caller's effective CanExport/CanImport — a User's own-∪-groups rights (ADR "Enforce group system
    // rights for members") or a ServiceAccount's own column (ADR "Dedicated CanExport/CanImport rights").
    // Replaces the old tenant-admin-only gate on export/import so those can be delegated.
    private async Task<bool> HasExportRightAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanExport;
        }

        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts.Where(s => s.Id == serviceAccountId).Select(s => s.CanExport).SingleOrDefaultAsync(cancellationToken);
        }

        return false;
    }

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

    // The caller's CanManageRepositories system right (User own∪groups, or ServiceAccount) — gates demoting a
    // repository by moving a root document into a folder (ADR "Repository creation endpoint").
    private async Task<bool> HasManageRepositoriesRightAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageRepositories;
        }

        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts.Where(s => s.Id == serviceAccountId).Select(s => s.CanManageRepositories).SingleOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    // Refuses a mutation on a document frozen by an active legal hold (ADR "Legal hold & retention
    // enforcement"). Called at every alteration site — rename/move/mask/index-data/ocr; delete checks the
    // whole subtree separately.
    private async Task EnsureNotFrozenAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (await _legalHold.IsFrozenAsync(documentId, cancellationToken))
        {
            throw new DocumentUnderLegalHoldException();
        }
    }

    // Refuses a mutation on a document checked out by a DIFFERENT user — the full edit-lock (ADR "Document
    // check-out / check-in"). Called at every alteration site alongside EnsureNotFrozenAsync. The holder
    // proceeds; a ServiceAccount caller (no UserId) is never the holder, so any active checkout blocks it.
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

    // The retention schedule for the detail pane (ADR "Retention policies (auto-disposition)") — null when the
    // document has no mask or its mask carries no retention period. The clock starts at the record's own issuing
    // date (latest confirmed version's DocumentDate), falling back to when it was filed.
    private async Task<RetentionInfo?> BuildRetentionInfoAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var row = await (
            from d in _dbContext.Documents
            where d.Id == documentId && d.MaskVersionId != null
            join mv in _dbContext.MaskVersions on d.MaskVersionId equals mv.Id
            where mv.RetentionYears != null
            select new { d.CreatedAt, d.CurrentVersionId, RetentionYears = mv.RetentionYears!.Value }).FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        // Anchor on the current version's DocumentDate honoring the CurrentVersionId pointer (issue #265), else
        // the latest confirmed, falling back to when the document was filed.
        var currentVersion = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, row.CurrentVersionId, cancellationToken);
        var anchor = currentVersion?.DocumentDate ?? DateOnly.FromDateTime(row.CreatedAt.UtcDateTime);

        return new RetentionInfo
        {
            RetentionYears = row.RetentionYears,
            DispositionDate = anchor.AddYears(row.RetentionYears).ToString("yyyy-MM-dd"),
            SuspendedByHold = await _legalHold.IsFrozenAsync(documentId, cancellationToken),
        };
    }

    // TIFF extensions — the OCR-languages system field only applies to a document with a TIFF source version.
    private static readonly HashSet<string> TiffExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff" };

    // Plain mutable properties, not { get; init; } — System.Xml.Serialization.XmlSerializer (ADR
    // "JSON/XML content negotiation") needs a parameterless constructor and settable properties.
    public class DocumentResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        // Data-classification / sensitivity label (ADR "Configurable sensitivity labels + upload defaults") — the
        // per-tenant label id (null = None) + its name/colour + whether it triggers the watermark, for the
        // detail-pane picker + badge + preview watermark.
        public Guid? SensitivityLabelId { get; set; }
        public string SensitivityLabelName { get; set; } = "";
        public string? SensitivityLabelColor { get; set; }
        public bool SensitivityWatermark { get; set; }

        // True when this document is DIRECTLY in an active legal hold (ADR "Legal hold & retention
        // enforcement") — the detail-pane lock indicator.
        public bool OnLegalHold { get; set; }

        // Records-retention schedule for the detail pane (ADR "Retention policies (auto-disposition)") — null
        // when the document has no mask or its mask has no retention period.
        public RetentionInfo? Retention { get; set; }

        // Present when the document is checked out (ADR "Document check-out / check-in") — the lock indicator +
        // check-in / override affordance. Null when free.
        public CheckoutInfo? CheckedOut { get; set; }

        // The caller's own CanManagePermissions on this item (ADR "Manage-access UI for document/folder ACLs") —
        // gates the clients' "Manage access…" affordance without a trial 403.
        public bool CanManagePermissions { get; set; }

        // True when this item ignores its ancestors' ACL and uses only its own grants (ADR "Document ACL
        // inheritance resolution") — the read-only inheritance indicator in the Manage-access dialog.
        public bool BreaksInheritance { get; set; }
    }

    public class RetentionInfo
    {
        public int RetentionYears { get; set; }
        public string DispositionDate { get; set; } = "";
        public bool SuspendedByHold { get; set; }
    }

    public class CheckoutInfo
    {
        public Guid ByUserId { get; set; }
        public string ByName { get; set; } = "";
        public DateTimeOffset At { get; set; }

        // True when the caller is the lock holder (offer check-in); false = held by someone else (offer override).
        public bool ByMe { get; set; }
    }

    private record DocumentRow(string Name, Guid ConcurrencyToken, Guid? SensitivityLabelId, string? SensitivityLabelName, string? SensitivityLabelColor, bool SensitivityWatermark, bool BreaksInheritance);

    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await LoadForReadAsync(documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanSee)
        {
            return Forbid();
        }

        SetETag(document.ConcurrencyToken);

        var onLegalHold = await _dbContext.LegalHoldItems
            .AnyAsync(i => i.DocumentId == documentId && _dbContext.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null), cancellationToken);

        var checkedOut = await BuildCheckoutInfoAsync(documentId, cancellationToken);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(Get), new { documentId })!, "GET"),
            new("children", Url.Action(nameof(ListChildren), new { documentId })!, "GET"),
            new("mask", Url.Action(nameof(GetMask), new { documentId })!, "GET"),
            new("index-data", Url.Action(nameof(GetIndexData), new { documentId })!, "GET"),
            new("versions", $"/api/documents/{documentId}/versions", "GET"),
            new("references", $"/api/documents/{documentId}/references", "GET"),
            new("referencing-folders", Url.Action(nameof(ListReferencingFolders), new { documentId })!, "GET"),
            new("move", Url.Action(nameof(Move), new { documentId })!, "PUT"),
            new("set-primary-location", Url.Action(nameof(SetPrimaryLocation), new { documentId })!, "PUT"),
            new("assignable-reviewers", Url.Action(nameof(AssignableReviewers), new { documentId })!, "GET"),
        };

        // Check-out affordances (ADR "Document check-out / check-in"): offer check-out when it's free and the
        // caller can edit content; offer check-in when the caller holds the lock or can override someone else's.
        if (checkedOut is null && rights.CanEditContent)
        {
            links.Add(new Link("checkout", $"/api/documents/{documentId}/checkout", "PUT"));
        }
        else if (checkedOut is { ByMe: true }
                 || (checkedOut is not null && _currentUserAccessor.UserId is { } uid
                     && (await _userSystemRights.GetEffectiveSystemRightsAsync(uid, cancellationToken)).CanOverrideCheckout))
        {
            links.Add(new Link("checkin", $"/api/documents/{documentId}/checkout", "DELETE"));
        }

        return Ok(new DocumentResource
        {
            Id = documentId,
            Name = document.Name,
            SensitivityLabelId = document.SensitivityLabelId,
            SensitivityLabelName = document.SensitivityLabelName ?? "",
            SensitivityLabelColor = document.SensitivityLabelColor,
            SensitivityWatermark = document.SensitivityWatermark,
            OnLegalHold = onLegalHold,
            CheckedOut = checkedOut,
            Retention = await BuildRetentionInfoAsync(documentId, cancellationToken),
            CanManagePermissions = rights.CanManagePermissions,
            BreaksInheritance = document.BreaksInheritance,
            Links = links,
        });
    }

    // The check-out state block for a document (ADR "Document check-out / check-in"), or null when not checked
    // out. ByMe distinguishes the caller's own lock (offer check-in) from someone else's (offer override).
    private async Task<CheckoutInfo?> BuildCheckoutInfoAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var row = await _dbContext.Documents
            .Where(d => d.Id == documentId && d.CheckedOutByUserId != null)
            .Select(d => new { HolderId = d.CheckedOutByUserId!.Value, At = d.CheckedOutAt!.Value })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var byName = await _dbContext.Users.Where(u => u.Id == row.HolderId).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken) ?? "";
        return new CheckoutInfo
        {
            ByUserId = row.HolderId,
            ByName = byName,
            At = row.At,
            ByMe = row.HolderId == _currentUserAccessor.UserId,
        };
    }

    // The candidate reviewers for submitting this document into the approval workflow (ADR "Workflow
    // assignable-reviewers endpoint") — a light catalog any editor can read, unlike GET /api/users which needs
    // CanManageUsers. Returns every active tenant user (id + display name only); submit still validates the
    // chosen reviewer has CanReadContent (400 INVALID_REVIEWER otherwise). Gated on CanEditContent (the same
    // right as submitting). A small bounded catalog, so not paginated (like GET /api/masks).
    [HttpGet("assignable-reviewers")]
    public async Task<IActionResult> AssignableReviewers(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        var reviewers = await _dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new ReviewerResource { Id = u.Id, DisplayName = u.DisplayName })
            .ToListAsync(cancellationToken);

        return Ok(new AssignableReviewersResource
        {
            Reviewers = reviewers,
            Links = [new Link("self", $"/api/documents/{documentId}/assignable-reviewers", "GET")],
        });
    }

    [HttpHead("assignable-reviewers")]
    public async Task<IActionResult> AssignableReviewersHead(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent ? NoContent() : Forbid();
    }

    public class AssignableReviewersResource : HypermediaResource
    {
        public List<ReviewerResource> Reviewers { get; set; } = [];
    }

    public class ReviewerResource
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = "";
    }

    // The item's ancestor folders, repository-root first down to its immediate parent (the item itself excluded) —
    // so a client can reveal it in the lazy-loaded tree: expand each ancestor, then select the parent (issue #340).
    // Requires CanSee on the item; the ancestors are the folders it already lives under, so no extra per-ancestor
    // check. An item filed at a repository root returns an empty list.
    [HttpGet("ancestors")]
    public async Task<IActionResult> ListAncestors(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var ancestors = new List<AncestorResource>();
        var currentId = await _dbContext.Documents
            .Where(d => d.Id == documentId).Select(d => d.ParentId).FirstAsync(cancellationToken);
        // Walk up ParentId to the root. The guard is a defensive backstop — SaveChanges already forbids cycles.
        for (var guard = 0; currentId is { } id && guard < 256; guard++)
        {
            var folder = await _dbContext.Documents
                .Where(d => d.Id == id).Select(d => new { d.Id, d.Name, d.ParentId }).FirstOrDefaultAsync(cancellationToken);
            if (folder is null)
            {
                break;
            }

            ancestors.Add(new AncestorResource { Id = folder.Id, Name = folder.Name });
            currentId = folder.ParentId;
        }

        ancestors.Reverse(); // repository-root first
        return Ok(new AncestorsResource
        {
            Ancestors = ancestors,
            Links = [new Link("self", $"/api/documents/{documentId}/ancestors", "GET")],
        });
    }

    [HttpHead("ancestors")]
    public async Task<IActionResult> AncestorsHead(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return await CanSeeAsync(documentId, cancellationToken) ? NoContent() : Forbid();
    }

    public class AncestorsResource : HypermediaResource
    {
        public List<AncestorResource> Ancestors { get; set; } = [];
    }

    public class AncestorResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }

    // Lets a client check the current ETag before a PUT without transferring the full representation —
    // a separate action, since ASP.NET Core doesn't automatically strip GET's body for a HEAD request.
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await LoadForReadAsync(documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        SetETag(document.ConcurrencyToken);

        return NoContent();
    }

    // Plain mutable class, not a record — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class RenameRequest
    {
        public string Name { get; set; } = "";
    }

    // PUT, not PATCH — RenameRequest.Name is the full intended value of the field this endpoint owns, not
    // a delta, so PUT's "here is what this resource should now be" contract fits and gets idempotency for
    // free. See ADR "DocumentVersionsController resource-oriented redesign".
    [HttpPut]
    public async Task<IActionResult> Rename(Guid documentId, [FromBody] RenameRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanEditIndexData)
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        document.Name = request.Name;

        // The textbook EF Core concurrency pattern: set the tracked entity's ORIGINAL ConcurrencyToken to
        // the client-supplied If-Match value, so the generated UPDATE's WHERE clause is checked against
        // it rather than whatever was actually loaded — if the real stored value has since changed, zero
        // rows are affected and EF Core throws DbUpdateConcurrencyException. See ADR "ETag / If-Match
        // optimistic concurrency".
        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForDocument();
        }

        await _queue.EnqueueAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentRenamed, "Document", documentId, document.Name, $"Renamed to '{document.Name}'", cancellationToken: cancellationToken);
        SetETag(document.ConcurrencyToken);

        return Ok(new DocumentResource
        {
            Id = documentId,
            Name = document.Name,
            Links = [new Link("self", Url.Action(nameof(Get), new { documentId })!, "GET")],
        });
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

        if (!await HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);

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

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanSee)
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

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanSee)
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

        if (!await HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);

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
        if (!await HasImportRightAsync(cancellationToken))
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
        return Ok(new DocumentResource
        {
            Id = doc.Id,
            Name = doc.Name,
            Links = [new Link("self", $"/api/documents/{doc.Id}", "GET")],
        });
    }

    [HttpHead("/api/documents/by-origin/{originTenantId:guid}/{originDocumentId:guid}")]
    public async Task<IActionResult> HeadByOrigin(Guid originTenantId, Guid originDocumentId, CancellationToken cancellationToken)
    {
        if (!await HasImportRightAsync(cancellationToken))
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

    public class MoveRequest
    {
        public Guid ParentId { get; set; }
    }

    // Moves (reparents) this item into another folder — see ADR "Desktop drag-and-drop move and reference".
    // Requires CanMove on the item + CanCreateSubItems on the target folder, and If-Match (a reparent is a
    // mutation, same concurrency contract as Rename). SaveChanges' existing sibling-name guard surfaces a
    // clash as 409; the into-own-subtree cycle is pre-checked here for a clean 400 rather than the generic
    // name-conflict path.
    [HttpPut("parent")]
    public async Task<IActionResult> Move(Guid documentId, [FromBody] MoveRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        // The target folder must exist in the caller's tenant (the tenant query filter scopes this lookup).
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == request.ParentId, cancellationToken))
        {
            throw new MoveTargetNotFoundException();
        }

        var itemRights = await GetCallerRightsAsync(documentId, cancellationToken);
        var targetRights = await GetCallerRightsAsync(request.ParentId, cancellationToken);

        if (!itemRights.CanMove || !targetRights.CanCreateSubItems)
        {
            return Forbid();
        }

        // Moving a root document (a repository, ParentId == null) into a folder demotes the repository — a structural
        // change gated on CanManageRepositories, beyond the per-document CanMove (ADR "Repository creation endpoint").
        if (document.ParentId is null && !await HasManageRepositoriesRightAsync(cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        // Can't move an item into itself or into its own subtree (that would orphan a cycle).
        if (await IsAncestorOrSelfAsync(documentId, request.ParentId, cancellationToken))
        {
            throw new InvalidMoveTargetException();
        }

        document.ParentId = request.ParentId;
        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForDocument();
        }
        catch (InvalidOperationException)
        {
            throw DocumentNameConflictException.OnTargetFolder();
        }

        await _queue.EnqueueAsync(documentId, cancellationToken);
        SetETag(document.ConcurrencyToken);

        await _audit.RecordAsync(AuditActions.DocumentMoved, "Document", documentId, document.Name, cancellationToken: cancellationToken);

        return Ok(new DocumentResource
        {
            Id = documentId,
            Name = document.Name,
            Links = [new Link("self", Url.Action(nameof(Get), new { documentId })!, "GET")],
        });
    }

    public class SetPrimaryLocationRequest
    {
        public Guid FolderId { get; set; }
    }

    // Promotes a referenced folder to be the document's primary location (ADR 0506): atomically moves the real
    // document into {FolderId} and leaves a reference behind at the former parent, so the set of places the
    // document appears is unchanged — only which one is the real home changes. Composes the Move + place-reference
    // primitives in a single SaveChanges so they can't half-apply. Same guards as Move (CanMove on the item +
    // CanCreateSubItems on the target AND on the old parent — the left-behind reference is a create there —
    // legal-hold / check-out / If-Match / cycle). A redundant target-side reference (the shortcut being promoted)
    // is dropped, since the document now really lives there.
    [HttpPut("primary-location")]
    public async Task<IActionResult> SetPrimaryLocation(Guid documentId, [FromBody] SetPrimaryLocationRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        // A repository root has no primary home to demote to a reference.
        if (document.ParentId is not { } oldParentId)
        {
            throw new CannotPromoteRepositoryRootException();
        }

        if (request.FolderId == oldParentId)
        {
            throw new PrimaryLocationUnchangedException();
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == request.FolderId, cancellationToken))
        {
            throw new MoveTargetNotFoundException();
        }

        var itemRights = await GetCallerRightsAsync(documentId, cancellationToken);
        var targetRights = await GetCallerRightsAsync(request.FolderId, cancellationToken);
        var oldParentRights = await GetCallerRightsAsync(oldParentId, cancellationToken);

        // CanMove to re-home the item, CanCreateSubItems on the target (as Move requires) AND on the old parent
        // (the left-behind reference is created there).
        if (!itemRights.CanMove || !targetRights.CanCreateSubItems || !oldParentRights.CanCreateSubItems)
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        if (await IsAncestorOrSelfAsync(documentId, request.FolderId, cancellationToken))
        {
            throw new InvalidMoveTargetException();
        }

        // Leave a reference at the former home — unless one already exists there (the unique index would reject a
        // duplicate).
        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();
        var alreadyReferencedAtOldParent = await _dbContext.DocumentReferences
            .AnyAsync(r => r.ParentFolderId == oldParentId && r.TargetDocumentId == documentId, cancellationToken);
        if (!alreadyReferencedAtOldParent)
        {
            _dbContext.DocumentReferences.Add(new DocumentReference
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                ParentFolderId = oldParentId,
                TargetDocumentId = documentId,
                CreatedByUserId = createdByUserId,
                CreatedByServiceAccountId = createdByServiceAccountId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        // The reference we're promoting (target folder → this doc) is now redundant — the doc really lives there.
        var redundantReference = await _dbContext.DocumentReferences
            .SingleOrDefaultAsync(r => r.ParentFolderId == request.FolderId && r.TargetDocumentId == documentId, cancellationToken);
        var removedRedundantReference = redundantReference is not null;
        if (redundantReference is not null)
        {
            _dbContext.DocumentReferences.Remove(redundantReference);
        }

        document.ParentId = request.FolderId;
        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForDocument();
        }
        catch (InvalidOperationException)
        {
            throw DocumentNameConflictException.OnTargetFolder();
        }

        await _queue.EnqueueAsync(documentId, cancellationToken);
        SetETag(document.ConcurrencyToken);

        await _audit.RecordAsync(AuditActions.DocumentMoved, "Document", documentId, document.Name, "Primary location changed", cancellationToken: cancellationToken);
        if (!alreadyReferencedAtOldParent)
        {
            await _audit.RecordAsync(AuditActions.ReferenceAdded, "Document", documentId, document.Name, "Reference left at former primary location", cancellationToken: cancellationToken);
        }

        if (removedRedundantReference)
        {
            await _audit.RecordAsync(AuditActions.ReferenceRemoved, "Document", documentId, document.Name, "Redundant reference removed at new primary location", cancellationToken: cancellationToken);
        }

        return Ok(new DocumentResource
        {
            Id = documentId,
            Name = document.Name,
            Links = [new Link("self", Url.Action(nameof(Get), new { documentId })!, "GET")],
        });
    }

    // Check-out (acquire the exclusive edit lock) — ADR "Document check-out / check-in". User-only (a
    // ServiceAccount can't hold a lock); requires CanEditContent + at least one confirmed version (there must
    // be something to edit); 409 if already held by someone else; idempotent no-op if already held by the
    // caller. No If-Match — this is a lock action, not a content edit (like POST restore).
    [HttpPut("checkout")]
    public async Task<IActionResult> CheckOut(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid(); // only an interactive User can check a document out
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        if (document.CheckedOutByUserId is { } holder && holder != userId)
        {
            throw new DocumentAlreadyCheckedOutException();
        }

        if (!await _dbContext.DocumentVersions.AnyAsync(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed, cancellationToken))
        {
            throw new NothingToCheckOutException();
        }

        if (document.CheckedOutByUserId is null)
        {
            document.CheckedOutByUserId = userId;
            document.CheckedOutAt = DateTimeOffset.UtcNow;
            document.CheckoutReminderSentAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync(AuditActions.DocumentCheckedOut, "Document", documentId, document.Name, cancellationToken: cancellationToken);
        }

        SetETag(document.ConcurrencyToken);
        return Ok(new DocumentResource { Id = documentId, Name = document.Name });
    }

    // Check-in / unlock / discard / override (release the lock) — ADR "Document check-out / check-in". The
    // holder releases their own lock; a CanOverrideCheckout holder can force-release someone else's ("break
    // the lock"). The new version, if any, was uploaded through the normal versions flow while the lock was
    // held — release does not itself create a version. Idempotent when not checked out.
    [HttpDelete("checkout")]
    public async Task<IActionResult> CheckIn(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (document.CheckedOutByUserId is not { } holder)
        {
            return NoContent(); // not checked out — nothing to release
        }

        var isOverride = holder != userId;
        if (isOverride)
        {
            var rights = await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken);
            if (!rights.CanOverrideCheckout)
            {
                return Forbid(); // only the holder or a CanOverrideCheckout holder may release
            }
        }

        var holderName = await _dbContext.Users.Where(u => u.Id == holder).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken);
        document.CheckedOutByUserId = null;
        document.CheckedOutAt = null;
        document.CheckoutReminderSentAt = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Releasing ends the check-out, so the holder's cloud working-copy stash is no longer needed (ADR
        // "Check-out working-copy stash + exit guard") — remove it best-effort. Keyed by the HOLDER's user id
        // (an override releases someone else's lock, so their stash is what's cleared).
        try
        {
            await _objectStorage.DeleteObjectAsync(CheckoutsController.StashKey(document.TenantId, holder, documentId), cancellationToken);
        }
        catch (Exception)
        {
            // Best-effort — an orphaned stash object is harmless.
        }

        await _audit.RecordAsync(
            isOverride ? AuditActions.DocumentCheckoutOverridden : AuditActions.DocumentCheckedIn,
            "Document", documentId, document.Name,
            isOverride ? $"released the check-out held by {holderName}" : null,
            cancellationToken: cancellationToken);

        SetETag(document.ConcurrencyToken);
        return NoContent();
    }

    // Soft delete (sets DeletedAt, ADR "Document recycle-bin data shape") — cascades recursively to every
    // descendant in the same operation, so the whole subtree moves to the recycle bin together (ADR
    // "Document delete/restore (recycle bin) implementation"). Requires If-Match like PUT, consistent with
    // ADR 0003's "ETag/If-Match on every mutation" commitment.
    [HttpDelete]
    public async Task<IActionResult> Delete(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanDelete)
        {
            return Forbid();
        }

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        var toDelete = await CollectSubtreeAsync(documentId, document, cancellationToken);

        // A legal hold freezes deletion: refuse if the target is under an ancestor hold, or any document in the
        // subtree being cascade-deleted is itself directly held (ADR "Legal hold & retention enforcement").
        if (await _legalHold.IsFrozenAsync(documentId, cancellationToken)
            || await _legalHold.AnyDirectlyHeldAsync(toDelete.Select(d => d.Id).ToList(), cancellationToken))
        {
            throw DocumentUnderLegalHoldException.ForDeletion();
        }

        // The full edit-lock blocks deletion too: refuse if the target or any document in the cascade is
        // checked out by a DIFFERENT user (ADR "Document check-out / check-in").
        if (toDelete.Any(d => d.CheckedOutByUserId is { } h && h != _currentUserAccessor.UserId))
        {
            throw DocumentCheckedOutException.ForDeletion();
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var doc in toDelete)
        {
            doc.DeletedAt = now;
        }

        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForDocument();
        }

        foreach (var doc in toDelete)
        {
            await _queue.EnqueueAsync(doc.Id, cancellationToken);
        }

        await _audit.RecordAsync(AuditActions.DocumentDeleted, "Document", documentId, document.Name,
            toDelete.Count > 1 ? $"cascade: {toDelete.Count} items" : null, cancellationToken: cancellationToken);

        return NoContent();
    }

    // Restores a soft-deleted document (and, recursively, every descendant that was cascade-deleted with
    // it) — the natural inverse of DELETE, reusing the same CanDelete right rather than a new one (ADR
    // "Recycle bin restore workflow"). Only the top-level target may need reparenting to a "Recovered
    // Items" folder: cascaded descendants' ParentId always points within the subtree being restored
    // together, so it's already valid by construction. Idempotent: restoring an already-active document is
    // a no-op that returns its current state.
    [HttpPost("restore")]
    public async Task<IActionResult> Restore(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanDelete)
        {
            return Forbid();
        }

        // The restore mechanics (reparent-to-Recovered-Items if the original parent is gone, un-delete the whole
        // subtree, re-index) live in the shared DocumentRestorer (ADR "Bulk restore from the recycle bin");
        // restoring an already-active document is an idempotent no-op (Restored == false → no audit).
        var (userId, serviceAccountId) = GetCallerIdentity();
        if (await _restorer.RestoreAsync(document, userId, serviceAccountId, cancellationToken))
        {
            await _audit.RecordAsync(AuditActions.DocumentRestored, "Document", documentId, document.Name, cancellationToken: cancellationToken);
        }

        return Ok(new DocumentResource
        {
            Id = documentId,
            Name = document.Name,
            Links = [new Link("self", Url.Action(nameof(Get), new { documentId })!, "GET")],
        });
    }

    // Permanently removes a recycle-bin document + its soft-deleted subtree (blobs + rows + search index),
    // irreversibly. Tenant-admin-only; refused for an active (not-recycled) document or one under legal hold.
    // A destructive action sub-resource (POST), like restore — not the soft-delete DELETE. See ADR "Manual
    // hard-delete / purge".
    [HttpPost("purge")]
    public async Task<IActionResult> Purge(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var subtree = await _purger.CollectSubtreeAsync(documentId, cancellationToken);
        if (subtree is null)
        {
            return NotFound();
        }

        if (subtree[0].DeletedAt is null)
        {
            throw new CannotPurgeActiveException();
        }

        var ids = subtree.Select(d => d.Id).ToList();
        if (await _purger.AnyHeldAsync(ids, cancellationToken))
        {
            throw DocumentUnderLegalHoldException.ForPurge();
        }

        var purged = await _purger.PurgeAsync(subtree, cancellationToken);
        foreach (var (id, name) in purged)
        {
            await _audit.RecordAsync(AuditActions.DocumentPurged, "Document", id, name, cancellationToken: cancellationToken);
        }

        return NoContent();
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
        if (!await HasExportRightAsync(cancellationToken))
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
        if (!await HasExportRightAsync(cancellationToken))
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
        if (!await HasImportRightAsync(cancellationToken))
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
            links = new[] { new Link("self", Url.Action(nameof(Get), new { documentId = result.RootDocumentId })!, "GET") },
        };
    }

    // Reduces a document name to a safe download-filename stem (the header value can't carry quotes/newlines).
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "export" : cleaned;
    }

    // Iterative level-by-level traversal (not a raw recursive CTE, to stay provider-agnostic) collecting a
    // document and every descendant, for cascade delete. (The restore path's own subtree walk lives in
    // DocumentRestorer.)
    private async Task<List<Document>> CollectSubtreeAsync(Guid rootId, Document root, CancellationToken cancellationToken)
    {
        var subtree = new List<Document> { root };
        var currentLevelIds = new List<Guid> { rootId };

        while (currentLevelIds.Count > 0)
        {
            var children = await _dbContext.Documents
                .Where(d => d.ParentId != null && currentLevelIds.Contains(d.ParentId!.Value))
                .ToListAsync(cancellationToken);

            if (children.Count == 0)
            {
                break;
            }

            subtree.AddRange(children);
            currentLevelIds = children.Select(c => c.Id).ToList();
        }

        return subtree;
    }

    public class DocumentSummaryResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        // Presentation metadata (ADR "Blazor repository/document browsing", "Workbench pane content fixes"):
        // HasVersions picks the icon (folder when false, downloadable document when true — a folder is a
        // Document with zero versions); HasChildren = any child; HasSubfolders = a child that is itself a
        // folder, which is what governs the folder tree's expand caret (the tree shows only folders).
        // Computed, never stored.
        public bool HasChildren { get; set; }

        public bool HasVersions { get; set; }

        public bool HasSubfolders { get; set; }

        // True when at least one DocumentReference targets this item — drives the "References …" affordance.
        // See ADR "References-of-an-item list". Computed in SQL, never stored.
        public bool HasReferences { get; set; }

        // True when this item is DIRECTLY in an active legal hold (ADR "Legal hold & retention enforcement") —
        // drives the lock indicator. A cheap subquery; a descendant frozen only via an ancestor hold won't set
        // this on its own row (but its mutations are still refused). Computed in SQL, never stored.
        public bool OnLegalHold { get; set; }

        // Check-out state (ADR "Document check-out / check-in") — CheckedOut drives the lock glyph, CheckedOutByMe
        // distinguishes the caller's own lock. Computed, never stored.
        public bool CheckedOut { get; set; }

        public bool CheckedOutByMe { get; set; }

        // The display name of the lock holder (ADR "Check-out working-copy stash") — the clients prefix a
        // checked-out row with "[name] ". Empty when not checked out.
        public string CheckedOutByName { get; set; } = "";

        // The latest confirmed version's file extension (e.g. ".zip") — Document.Name is a bare stem (ADR 0277),
        // so the client reads the type from here (e.g. to browse a zip). Empty for folders / version-less docs.
        public string FileExtension { get; set; } = "";

        // List-row columns (ADR "List-row columns and sorting") — the document type (the assigned mask's name;
        // "" for a folder / unclassified), the latest confirmed version's document date + byte size, and the
        // tags. All derived/projected, never new stored columns.
        public string DocumentType { get; set; } = "";

        public DateOnly? DocumentDate { get; set; }

        public long? SizeBytes { get; set; }

        public List<string> Tags { get; set; } = [];

        // The data-classification sensitivity label (ADR "Configurable sensitivity labels + upload defaults") —
        // the list-row badge: the label id (null = None, no badge), its name + colour. Derived, never stored here.
        public Guid? SensitivityLabelId { get; set; }

        public string SensitivityLabelName { get; set; } = "";

        public string? SensitivityLabelColor { get; set; }

        // Count of confirmed versions — gates the desktop "Compare versions" action (needs >= 2 to have anything
        // to diff), ADR "Compare-versions gating + default". Projected (one COUNT subquery), never stored.
        public int VersionCount { get; set; }

        // The latest confirmed version's CreatedAt (its filing timestamp) — the sort key for the "Created" folder
        // contents-sort order (ADR "Per-folder contents sort order"). Null for a folder / version-less doc.
        public DateTimeOffset? VersionCreatedAt { get; set; }
    }

    private record DocumentSummaryRow(Guid Id, string Name, DateTimeOffset CreatedAt, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, bool OnLegalHold, Guid? CheckedOutByUserId, string? CheckedOutByName, string? LatestObjectKey, string? DocumentType, DateOnly? DocumentDate, long? SizeBytes, Guid? SensitivityLabelId, string? SensitivityLabelName, string? SensitivityLabelColor, int VersionCount, DateTimeOffset? VersionCreatedAt);

    public class DocumentChildrenResource : HypermediaResource
    {
        public List<DocumentSummaryResource> Children { get; set; } = [];

        // The listed folder's persisted default contents sort order (ADR "Per-folder contents sort order"). The
        // clients apply it (folders-first) as the default order when the folder is opened; a column-header click
        // is an ephemeral override. Serialized as the int enum value (Name=0/DocumentDate=1/Created=2).
        public FolderContentsSortOrder ContentsSortOrder { get; set; }
    }

    // "children", not "documents" — /documents/{id}/documents would repeat the same word despite being
    // nominally consistent with /repositories/{id}/documents (a Repository -> documents relationship is
    // container-to-item, while Document -> children is item-to-item, so the two were never perfectly
    // parallel anyway). See ADR "Nested Document creation".
    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet("children")]
    public async Task<IActionResult> ListChildren(Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        // Fetch the listed folder's persisted contents sort order (ADR "Per-folder contents sort order") — also
        // serves as the existence check (null = no such document).
        var folderSortOrder = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => (FolderContentsSortOrder?)d.ContentsSortOrder)
            .FirstOrDefaultAsync(cancellationToken);
        if (folderSortOrder is null)
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        // Clearance enforcement (ADR "Sensitivity clearance enforcement"): drop children labelled above the
        // caller's clearance so they're hidden from listings entirely. No-op unless the tenant enforces it.
        var clearance = await _clearanceScope.ResolveAsync(cancellationToken);
        var query = clearance.Filter(_dbContext.Documents.Where(d => d.ParentId == documentId));

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(d => d.CreatedAt > cursorCreatedAt || (d.CreatedAt == cursorCreatedAt && d.Id > cursorId));
        }

        // HasChildren/HasVersions are computed in SQL (two .Any() subqueries per row) — never stored. See
        // ADR "Blazor repository/document browsing".
        var fetched = await query
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Take(pageSize + 1)
            .Select(d => new DocumentSummaryRow(
                d.Id,
                d.Name,
                d.CreatedAt,
                _dbContext.Documents.Any(c => c.ParentId == d.Id),
                _dbContext.DocumentVersions.Any(v => v.DocumentId == d.Id),
                _dbContext.Documents.Any(c => c.ParentId == d.Id && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id)),
                _dbContext.DocumentReferences.Any(r => r.TargetDocumentId == d.Id),
                _dbContext.LegalHoldItems.Any(i => i.DocumentId == d.Id && _dbContext.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null)),
                d.CheckedOutByUserId,
                _dbContext.Users.Where(u => u.Id == d.CheckedOutByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                // The CURRENT version's object key — the pinned version if CurrentVersionId is set (issue #265),
                // else the latest confirmed. Its extension is the document's file type (Name is a bare stem, ADR
                // 0277), letting the client detect e.g. a .zip to browse.
                d.CurrentVersionId != null
                    ? _dbContext.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => v.ObjectKey).FirstOrDefault()
                    : _dbContext.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => v.ObjectKey).FirstOrDefault(),
                // List-row columns (ADR "List-row columns and sorting"): the assigned mask's name, and the CURRENT
                // version's document date + size (pointer-aware, issue #265).
                _dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => mv.Name).FirstOrDefault(),
                d.CurrentVersionId != null
                    ? _dbContext.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => (DateOnly?)v.DocumentDate).FirstOrDefault()
                    : _dbContext.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => (DateOnly?)v.DocumentDate).FirstOrDefault(),
                d.CurrentVersionId != null
                    ? _dbContext.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => v.SizeBytes).FirstOrDefault()
                    : _dbContext.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => v.SizeBytes).FirstOrDefault(),
                d.SensitivityLabelId,
                d.SensitivityLabelId == null ? null : _dbContext.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Name).FirstOrDefault(),
                d.SensitivityLabelId == null ? null : _dbContext.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Color).FirstOrDefault(),
                // Confirmed-version count — gates the desktop "Compare versions" action (needs >= 2).
                _dbContext.DocumentVersions.Count(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed),
                // The CURRENT version's CreatedAt (filing timestamp) — the "Created" contents-sort key (pointer-aware).
                d.CurrentVersionId != null
                    ? _dbContext.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => (DateTimeOffset?)v.CreatedAt).FirstOrDefault()
                    : _dbContext.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => (DateTimeOffset?)v.CreatedAt).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        // Tags per row (ADR "List-row columns and sorting") — a single batched query over the page's ids.
        var tagsByDoc = await LoadTagsForPageAsync(page.Select(p => p.Id).ToList(), cancellationToken);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(ListChildren), new { documentId, cursor, limit = pageSize })!, "GET"),
            new("create-child", Url.Action(nameof(CreateChild), new { documentId })!, "POST"),
        };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(ListChildren), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new DocumentChildrenResource
        {
            Children = page.Select(d => new DocumentSummaryResource
            {
                Id = d.Id,
                Name = d.Name,
                HasChildren = d.HasChildren,
                HasVersions = d.HasVersions,
                HasSubfolders = d.HasSubfolders,
                HasReferences = d.HasReferences,
                OnLegalHold = d.OnLegalHold,
                CheckedOut = d.CheckedOutByUserId != null,
                CheckedOutByMe = d.CheckedOutByUserId == _currentUserAccessor.UserId,
                CheckedOutByName = d.CheckedOutByName ?? "",
                FileExtension = Path.GetExtension(d.LatestObjectKey ?? ""),
                DocumentType = d.DocumentType ?? "",
                DocumentDate = d.DocumentDate,
                SizeBytes = d.SizeBytes,
                Tags = tagsByDoc.TryGetValue(d.Id, out var tags) ? tags : [],
                SensitivityLabelId = d.SensitivityLabelId,
                SensitivityLabelName = d.SensitivityLabelName ?? "",
                SensitivityLabelColor = d.SensitivityLabelColor,
                VersionCount = d.VersionCount,
                VersionCreatedAt = d.VersionCreatedAt,
                Links = new List<Link> { new("self", $"/api/documents/{d.Id}", "GET") },
            }).ToList(),
            ContentsSortOrder = folderSortOrder.Value,
            Links = links,
        });
    }

    // Batched tag lookup for a page of documents (ADR "List-row columns and sorting") — one query, grouped by
    // document, tags sorted; empty for a document with none.
    private async Task<Dictionary<Guid, List<string>>> LoadTagsForPageAsync(List<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        return (await _dbContext.DocumentTags
                .Where(t => documentIds.Contains(t.DocumentId))
                .OrderBy(t => t.Tag)
                .Select(t => new { t.DocumentId, t.Tag })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("children")]
    public async Task<IActionResult> HeadChildren(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    public class CreateChildRequest
    {
        public string Name { get; set; } = "";
    }

    [HttpPost("children")]
    public async Task<IActionResult> CreateChild(Guid documentId, [FromBody] CreateChildRequest request, CancellationToken cancellationToken)
    {
        var parent = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId })
            .SingleOrDefaultAsync(cancellationToken);

        if (parent is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanCreateSubItems)
        {
            return Forbid();
        }

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();

        var child = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = parent.TenantId,
            ParentId = documentId,
            Name = request.Name,
            // Assigned the Folder mask now; if a version is later added, finalize reclassifies it (ADR "Folder mask on folders").
            MaskVersionId = await Documents.FolderMask.CurrentVersionIdAsync(_dbContext, cancellationToken),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(child);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw DocumentNameConflictException.OnSameParent();
        }

        await _queue.EnqueueAsync(child.Id, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentCreated, "Document", child.Id, child.Name, cancellationToken: cancellationToken);

        var resource = new DocumentSummaryResource
        {
            Id = child.Id,
            Name = child.Name,
            Links = [new Link("self", $"/api/documents/{child.Id}", "GET")],
        };

        return CreatedAtAction(nameof(Get), new { documentId = child.Id }, resource);
    }

    // Plain mutable classes, not records — same XmlSerializer rationale as elsewhere.
    public class ReferencingFolderResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        // Full folder path, e.g. "Repositories / Contracts / 2026" — disambiguates same-named folders.
        public string Path { get; set; } = "";
    }

    // The document's real (primary) home folder — where it actually lives, as opposed to the folders that merely
    // reference it (ADR 0506). Null when the item is a repository root, or when the caller can't see the parent.
    public class PrimaryLocationResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string Path { get; set; } = "";
    }

    public class ReferencingFoldersResource : HypermediaResource
    {
        public PrimaryLocationResource? PrimaryLocation { get; set; }

        public List<ReferencingFolderResource> Folders { get; set; } = [];
    }

    private record ReferencingFolderRow(Guid ReferenceId, DateTimeOffset CreatedAt, Guid FolderId, string FolderName);

    // Lists every folder that holds a reference (shortcut) to this item — the inverse of
    // DocumentReferencesController's per-folder listing. See ADR "References-of-an-item list". Requires CanSee
    // on the item, and each referencing folder is filtered to those the caller can also see (so it can't leak
    // the existence of a folder the caller has no access to). Cursor-paginated over the DocumentReference row
    // (CreatedAt asc, Id asc), same per-item-filtered walk as RepositoriesController.List.
    [HttpGet("referencing-folders")]
    public async Task<IActionResult> ListReferencingFolders(Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // The document's real home (ADR 0506): its parent folder, shown as the prominent "Primary location" row.
        // Null when the item is a repository root (no parent) or when the caller can't see the parent — don't
        // leak a location the caller has no access to.
        var realParentId = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.ParentId)
            .SingleAsync(cancellationToken);

        PrimaryLocationResource? primaryLocation = null;
        if (realParentId is { } parentId && await CanSeeAsync(parentId, cancellationToken))
        {
            var parentName = await _dbContext.Documents
                .Where(d => d.Id == parentId)
                .Select(d => d.Name)
                .SingleAsync(cancellationToken);

            primaryLocation = new PrimaryLocationResource
            {
                Id = parentId,
                Name = parentName,
                Path = await BuildFolderPathAsync(parentId, cancellationToken),
                Links = [new Link("open", $"/api/documents/{parentId}", "GET")],
            };
        }

        var pageSize = PageSize.Resolve(limit);

        // The parent folder must still exist (the join to Documents applies the soft-delete filter).
        var query = _dbContext.DocumentReferences
            .Where(r => r.TargetDocumentId == documentId && _dbContext.Documents.Any(d => d.Id == r.ParentFolderId));

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(r => r.CreatedAt > cursorCreatedAt || (r.CreatedAt == cursorCreatedAt && r.Id > cursorId));
        }

        var candidates = await query
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new ReferencingFolderRow(
                r.Id,
                r.CreatedAt,
                r.ParentFolderId,
                _dbContext.Documents.Where(d => d.Id == r.ParentFolderId).Select(d => d.Name).FirstOrDefault()!))
            .ToListAsync(cancellationToken);

        var visible = new List<ReferencingFolderResource>();
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;
        var hasMore = false;

        foreach (var candidate in candidates)
        {
            if (visible.Count >= pageSize)
            {
                if (await CanSeeAsync(candidate.FolderId, cancellationToken))
                {
                    hasMore = true;
                    break;
                }

                lastCreatedAt = candidate.CreatedAt;
                lastId = candidate.ReferenceId;
                continue;
            }

            if (await CanSeeAsync(candidate.FolderId, cancellationToken))
            {
                visible.Add(new ReferencingFolderResource
                {
                    Id = candidate.FolderId,
                    Name = candidate.FolderName,
                    Path = await BuildFolderPathAsync(candidate.FolderId, cancellationToken),
                    Links = [new Link("open", $"/api/documents/{candidate.FolderId}", "GET")],
                });
            }

            lastCreatedAt = candidate.CreatedAt;
            lastId = candidate.ReferenceId;
        }

        var links = new List<Link> { new("self", Url.Action(nameof(ListReferencingFolders), new { documentId, cursor, limit = pageSize })!, "GET") };

        if (hasMore && lastCreatedAt is { } created && lastId is { } id)
        {
            var nextCursor = Cursor.Encode(created, id);
            links.Add(new Link("next", Url.Action(nameof(ListReferencingFolders), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new ReferencingFoldersResource { PrimaryLocation = primaryLocation, Folders = visible, Links = links });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead("referencing-folders")]
    public async Task<IActionResult> HeadReferencingFolders(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
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

        if (!await CanSeeAsync(documentId, cancellationToken))
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

        if (!await CanSeeAsync(documentId, cancellationToken))
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

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

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
            await _dbContext.SaveChangesAsync(cancellationToken);
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

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
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

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
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

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

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

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        document.MaskVersionId = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

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

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

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

        if (!await CanSeeAsync(documentId, cancellationToken))
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

        if (!await CanSeeAsync(documentId, cancellationToken))
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

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

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
            if (definition.DataType != FieldDataType.MultiSelect && field.Values.Count > 1)
            {
                throw new MultipleValuesNotAllowedException($"Field '{definition.Name}' does not allow multiple values.");
            }
        }

        var existingValues = await _dbContext.FieldValues.Where(v => v.DocumentId == documentId).ToListAsync(cancellationToken);
        _dbContext.FieldValues.RemoveRange(existingValues);

        foreach (var field in request.Fields)
        {
            foreach (var value in field.Values)
            {
                _dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = document.TenantId,
                    DocumentId = documentId,
                    FieldDefinitionId = field.FieldDefinitionId,
                    Value = value,
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
        var rows = await _dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Id, f.Name, v.Value })
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

    // Note: the mask / index-data / versions / comments reads serve soft-deleted (recycle-bin) documents (ADR
    // "Recycle bin tab") so the recycle-bin detail pane can inspect a deleted item; GET /documents/{id} itself
    // stays soft-delete-filtered (a "gone" document should 404, and the recycle-bin detail doesn't need it).
    private async Task<DocumentRow?> LoadForReadAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new DocumentRow(
                d.Name,
                d.ConcurrencyToken,
                d.SensitivityLabelId,
                d.SensitivityLabelId == null ? null : _dbContext.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Name).FirstOrDefault(),
                d.SensitivityLabelId == null ? null : _dbContext.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Color).FirstOrDefault(),
                d.SensitivityLabelId != null && _dbContext.SensitivityLabelDefinitions.Any(l => l.Id == d.SensitivityLabelId && l.Watermark),
                d.BreaksInheritance))
            .SingleOrDefaultAsync(cancellationToken);
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

    private async Task<bool> CanSeeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanSee;
    }

    private async Task<bool> CanEditIndexDataAsync(Guid documentId, CancellationToken cancellationToken)
    {
        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanEditIndexData;
    }

    // Returns whichever principal actually made this request, for Document/DocumentVersion creator
    // attribution (CreatedByUserId/CreatedByServiceAccountId, CHECK-constrained to exactly one).
    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity()
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (null, serviceAccountId);
        }

        return (_currentUserAccessor.UserId, null);
    }

    // Walks up from startId via ParentId; true if candidateAncestorId is startId itself or any of its
    // ancestors. Used to reject moving an item into itself or its own subtree. See ADR "Desktop
    // drag-and-drop move and reference".
    private async Task<bool> IsAncestorOrSelfAsync(Guid candidateAncestorId, Guid startId, CancellationToken cancellationToken)
    {
        Guid? currentId = startId;

        while (currentId is { } id)
        {
            if (id == candidateAncestorId)
            {
                return true;
            }

            currentId = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => d.ParentId)
                .SingleAsync(cancellationToken);
        }

        return false;
    }

    // Builds a folder's full display path, e.g. "Repositories / Contracts / 2026", by walking up ParentId.
    private async Task<string> BuildFolderPathAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        Guid? currentId = folderId;

        while (currentId is { } id)
        {
            var node = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => new { d.Name, d.ParentId })
                .SingleAsync(cancellationToken);
            names.Add(node.Name);
            currentId = node.ParentId;
        }

        names.Reverse();
        return string.Join(" / ", names.Prepend("Repositories"));
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
