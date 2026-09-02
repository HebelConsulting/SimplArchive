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
    private readonly Documents.DocumentAccessService _access;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IDocumentIndexQueue _queue;

    public DocumentsController(
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        ICurrentUserAccessor currentUserAccessor,
        IDocumentIndexQueue queue,
        IAuditRecorder audit,
        ILegalHoldService legalHold,
        IUserSystemRightsResolver userSystemRights,
        ICurrentTenantAccessor currentTenantAccessor,
        Documents.DocumentMover mover,
        Documents.DocumentResourceLinks resourceLinks)
    {
        _currentTenantAccessor = currentTenantAccessor;
        _mover = mover;
        _resourceLinks = resourceLinks;
        _dbContext = dbContext;
        _access = access;
        _currentUserAccessor = currentUserAccessor;
        _queue = queue;
        _audit = audit;
        _legalHold = legalHold;
        _userSystemRights = userSystemRights;
    }

    private readonly IAuditRecorder _audit;
    private readonly ILegalHoldService _legalHold;
    private readonly IUserSystemRightsResolver _userSystemRights;

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
            // The shared rule, not a third copy of it (#871).
            DispositionDate = Documents.RetentionSchedule.DispositionDateOf(anchor, row.RetentionYears).ToString("yyyy-MM-dd"),
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

        public string Name { get; set; } = string.Empty;

        // Data-classification / sensitivity label (ADR "Configurable sensitivity labels + upload defaults") — the
        // per-tenant label id (null = None) + its name/colour + whether it triggers the watermark, for the
        // detail-pane picker + badge + preview watermark.
        public Guid? SensitivityLabelId { get; set; }
        public string SensitivityLabelName { get; set; } = string.Empty;
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

        /// <summary>
        /// Whether the caller may DELETE this item — the right the delete endpoint itself enforces.
        /// </summary>
        /// <remarks>
        /// A flag rather than a rel, and that is ADR 0719 deciding rather than taste: `DELETE` lives at this
        /// resource's own address, so a `delete` rel beside `self` would be the same URL under a second name
        /// with the method already saying which action it is.
        ///
        /// It exists because the destructive affordances were the half of ADR 0543 nobody converted (#858): the
        /// create path is gated on `create-child` while Delete was offered to anyone who could SEE a row, and
        /// answered with a 403 the menu had promised would not come.
        /// </remarks>
        public bool CanDelete { get; set; }

        /// <summary>
        /// Whether the caller may change this item's name, index data and — on a folder — its contents order:
        /// `CanEditIndexData`, the right the `PUT` on this address enforces.
        /// </summary>
        /// <remarks>
        /// Named after the RIGHT rather than after an affordance, so a client gate and the server's refusal
        /// cannot drift apart while both still look correct. Also a flag for ADR 0719's reason: that `PUT` is
        /// this resource's own address.
        /// </remarks>
        public bool CanEditIndexData { get; set; }

        /// <summary>May the caller create a plain child — a folder or an uploaded document — in this folder? (#854)</summary>
        /// <remarks>
        /// Replaces the `create-child` rel, which addressed the children collection that `children` already
        /// addresses (ADR 0719: one rel per resource, the method says the action). Both halves, as the rel here
        /// already did: the mask must admit a plain child AND the caller must hold <c>CanCreateSubItems</c>.
        /// </remarks>
        public bool CanCreateChildren { get; set; }

        // True when this item ignores its ancestors' ACL and uses only its own grants (ADR "Document ACL
        // inheritance resolution") — the read-only inheritance indicator in the Manage-access dialog.
        public bool BreaksInheritance { get; set; }

        // The order this FOLDER lists its contents in (ADR "Per-folder contents sort order"). Carried on the
        // document resource so a client showing a folder's details has it without listing the folder first: the
        // clients now show one detail pane for folders and documents alike, and a child folder's pane is opened
        // from its parent's listing, where the child's own setting has never been fetched. Meaningless for a
        // document, which lists nothing.
        public FolderContentsSortOrder ContentsSortOrder { get; set; }
    }

    public class RetentionInfo
    {
        public int RetentionYears { get; set; }
        public string DispositionDate { get; set; } = string.Empty;
        public bool SuspendedByHold { get; set; }
    }

    public class CheckoutInfo
    {
        public Guid ByUserId { get; set; }
        public string ByName { get; set; } = string.Empty;
        public DateTimeOffset At { get; set; }

        // True when the caller is the lock holder (offer check-in); false = held by someone else (offer override).
        public bool ByMe { get; set; }
    }

    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly Documents.DocumentMover _mover;
    private readonly Documents.DocumentResourceLinks _resourceLinks;

    private record DocumentRow(string Name, Guid ConcurrencyToken, Guid? SensitivityLabelId, string? SensitivityLabelName, string? SensitivityLabelColor, bool SensitivityWatermark, bool BreaksInheritance, FolderContentsSortOrder ContentsSortOrder, Guid? ParentId);

    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await LoadForReadAsync(documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanSee)
        {
            return Forbid();
        }

        SetETag(document.ConcurrencyToken);

        var onLegalHold = await _dbContext.LegalHoldItems
            .AnyAsync(i => i.DocumentId == documentId && _dbContext.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null), cancellationToken);

        var checkedOut = await BuildCheckoutInfoAsync(documentId, cancellationToken);

        // The document's external links (ADR 0546, issue #385). CONDITIONAL, unlike the rels around it: a missing
        // rel means "not available to you, here, now" (ADR 0543), so a client hides the affordance instead of
        // offering one that leads to a refusal. Two things have to hold — the tenant's master switch, and the
        // caller's right to read this document's content, which is what the linked GET itself requires. Whether
        // the caller may also CREATE a link is a separate question answered by "canCreate" on that resource, so a
        // reviewer who cannot share still reaches the list.
        //
        // Scoped by THIS document's TenantId, deliberately: Tenant is not ITenantScoped, so it carries no
        // automatic tenant query filter — an unqualified "any tenant allows external links" would answer for the
        // whole database and light the affordance up for every tenant as soon as one enabled it.
        //
        // A FOLDER is never shareable — POST answers CANNOT_SHARE_FOLDER — so the rel must not appear on one
        // either. Advertising it and refusing the click is precisely the shape ADR 0543 rules out: the client
        // would draw an affordance whose only outcome is a 400. "Has a confirmed version" is what separates a
        // document from a folder here, the same test the list uses to pick its icon.
        // The latest confirmed version's object key answers BOTH questions in one query: whether this is a
        // folder (no confirmed version) and whether it is a zip (issue #416). Both clients decided the latter by
        // string-comparing a file extension they carried around — an inference a rel should make for them.
        var latestKey = await _dbContext.DocumentVersions
            .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed)
            .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
            .Select(v => v.ObjectKey)
            .FirstOrDefaultAsync(cancellationToken);

        var isFolder = latestKey is null;
        var isArchive = latestKey is not null
            && Path.GetExtension(latestKey).Equals(".zip", StringComparison.OrdinalIgnoreCase);

        var externalLinksAllowed = !isFolder
            && rights.CanReadContent
            && await _dbContext.Tenants.AnyAsync(t => t.Id == _currentTenantAccessor.TenantId && t.AllowExternalLinks, cancellationToken);

        // The rels this caller may follow, and the one capability that is not a rel (ADR 0719). Assembled by
        // DocumentResourceLinks: 210 lines of conditional emission, each condition mirroring what its endpoint
        // enforces, which is the reasoning ADR 0543 requires and not the HTTP edge's business.
        var (links, canCreateChildren) = await _resourceLinks.BuildAsync(
            Url, documentId, document.ParentId, rights, checkedOut, isFolder, isArchive, externalLinksAllowed, cancellationToken);

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
            CanDelete = rights.CanDelete,
            CanEditIndexData = rights.CanEditIndexData,
            CanCreateChildren = canCreateChildren,
            BreaksInheritance = document.BreaksInheritance,
            ContentsSortOrder = document.ContentsSortOrder,
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

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
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

        return (await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent ? NoContent() : Forbid();
    }

    public class AssignableReviewersResource : HypermediaResource
    {
        public List<ReviewerResource> Reviewers { get; set; } = [];
    }

    public class ReviewerResource
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = string.Empty;
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

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
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

        return await _access.CanSeeAsync(documentId, cancellationToken) ? NoContent() : Forbid();
    }

    public class AncestorsResource : HypermediaResource
    {
        public List<AncestorResource> Ancestors { get; set; } = [];
    }

    public class AncestorResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
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

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
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
        public string Name { get; set; } = string.Empty;
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

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanEditIndexData)
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

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

        var itemRights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        var targetRights = await _access.GetCallerRightsAsync(request.ParentId, cancellationToken);

        if (!itemRights.CanMove || !targetRights.CanCreateSubItems)
        {
            return Forbid();
        }

        // Moving a root document (a repository, ParentId == null) into a folder demotes the repository — a structural
        // change gated on CanManageRepositories, beyond the per-document CanMove (ADR "Repository creation endpoint").
        if (document.ParentId is null && !await _access.HasManageRepositoriesRightAsync(cancellationToken))
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        // Can't move an item into itself or into its own subtree (that would orphan a cycle).
        if (await IsAncestorOrSelfAsync(documentId, request.ParentId, cancellationToken))
        {
            throw new InvalidMoveTargetException();
        }

        // Filing out of a mail inbox moves the BYTES too (#633) — before the save, so the key rewrites ride on
        // it and a refused move leaves the document addressing its original content.
        await _mover.RelocateContentForMoveAsync(documentId, request.ParentId, cancellationToken);

        document.ParentId = request.ParentId;
        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
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

        var itemRights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        var targetRights = await _access.GetCallerRightsAsync(request.FolderId, cancellationToken);
        var oldParentRights = await _access.GetCallerRightsAsync(oldParentId, cancellationToken);

        // CanMove to re-home the item, CanCreateSubItems on the target (as Move requires) AND on the old parent
        // (the left-behind reference is created there).
        if (!itemRights.CanMove || !targetRights.CanCreateSubItems || !oldParentRights.CanCreateSubItems)
        {
            return Forbid();
        }

        await _access.EnsureNotFrozenAsync(documentId, cancellationToken);
        await _access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

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
        var (createdByUserId, createdByServiceAccountId) = _access.GetCallerIdentity();
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

        // Promoting a reference RELOCATES the document, so it crosses the ephemeral boundary exactly as a move
        // does (#633). Easy to miss — nothing about "primary location" says "move" — which is why the seam is a
        // service every relocation routes through rather than a fix in the move handler.
        await _mover.RelocateContentForMoveAsync(documentId, request.FolderId, cancellationToken);

        document.ParentId = request.FolderId;
        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
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
                d.BreaksInheritance,
                d.ContentsSortOrder,
                d.ParentId))
            .SingleOrDefaultAsync(cancellationToken);
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

    private void SetETag(Guid concurrencyToken)
    {
        Response.Headers.ETag = $"\"{concurrencyToken}\"";
    }

    private static bool TryParseETag(string headerValue, out Guid token)
    {
        return Guid.TryParse(headerValue.Trim('"'), out token);
    }
}
