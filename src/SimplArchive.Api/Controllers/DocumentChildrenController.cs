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
/// The document's relationship listings and child creation: children (list/create) and referencing-folders.
/// Split out of DocumentsController (#466); routes unchanged — ListChildren/CreateChild check rights against
/// the parent document itself, since that is the container being read/written (ADR 0209).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class DocumentChildrenController : ControllerBase
{
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly Documents.DocumentAccessService _access;
    private readonly IAuditRecorder _audit;
    private readonly IDocumentIndexQueue _queue;
    private readonly Documents.IClearanceScopeResolver _clearanceScope;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public DocumentChildrenController(
        ICurrentUserAccessor currentUserAccessor,
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        IAuditRecorder audit,
        IDocumentIndexQueue queue,
        Documents.IClearanceScopeResolver clearanceScope,
        ICurrentTenantAccessor currentTenantAccessor)
    {
        _currentUserAccessor = currentUserAccessor;
        _dbContext = dbContext;
        _access = access;
        _audit = audit;
        _queue = queue;
        _clearanceScope = clearanceScope;
        _currentTenantAccessor = currentTenantAccessor;
    }

    public class DocumentSummaryResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        // Presentation metadata (ADR "Blazor repository/document browsing", "Workbench pane content fixes"):
        // HasVersions picks the icon (folder when false, downloadable document when true — a folder is a
        // Document with zero versions); HasChildren = anything filed here at all — a child document, a subfolder,
        // OR a reference filed into it (issue #376; it counted only child Documents before, so a folder holding
        // only shortcuts reported false); HasSubfolders = a child that is itself a folder, which is what governs
        // the folder tree's expand caret (the tree shows only folders).
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

    private record DocumentSummaryRow(Guid Id, string Name, DateTimeOffset CreatedAt, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, bool OnLegalHold, Guid? CheckedOutByUserId, string? CheckedOutByName, string? LatestObjectKey, string? DocumentType, DateOnly? DocumentDate, long? SizeBytes, Guid? SensitivityLabelId, string? SensitivityLabelName, string? SensitivityLabelColor, int VersionCount, DateTimeOffset? VersionCreatedAt, Guid? MaskId);

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

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
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
                // Child document/subfolder OR a reference filed into it (issue #376) — "is anything filed
                // here", which is what the empty-folder tree glyph and the open/navigate tests want.
                _dbContext.Documents.Any(c => c.ParentId == d.Id)
                    || _dbContext.DocumentReferences.Any(x => x.ParentFolderId == d.Id),
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
                    : _dbContext.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => (DateTimeOffset?)v.CreatedAt).FirstOrDefault(),
                // The assigned mask's ID, alongside the NAME two fields up. The name is for display and is
                // localised/renamable; the id is the stable thing a rule keys on — "Note Folder" became
                // "Notebook" without a single document moving precisely because the id did not change.
                _dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault()))
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
                // A listed item advertises its own UNCONDITIONAL sub-resources, not just self+chat (issue #416).
                //
                // Without these a client holding a listing has an id and no addresses, so every sub-resource call
                // it makes has to be composed from a template — which is most of what the ADR 0543 ledger is still
                // counting. Fetching `self` first instead would cost a round trip per row, and paying two calls to
                // follow one rel is the usual reason a codebase abandons hypermedia and goes back to string paths.
                //
                // Rels that depend on the CALLER'S RIGHTS or the item's STATE are deliberately absent —
                // checkout/checkin, external-links, acl-inheritance. Each is a question the full document
                // resource computes once and a listing would have to compute per row. A listing is the wrong
                // place to answer "may I?", so those affordances still require the resource itself, and their
                // absence here must NOT be read as "not available" (ADR 0543's rule applies to a resource, not
                // to a summary).
                //
                // A MASK-DERIVED rel is not one of those, and the distinction matters (#564). Whether a folder
                // holds sections and notes is a property of its mask, not of who is asking — and the query is
                // already joining MaskVersions to produce DocumentType, so it costs nothing to answer here.
                // The alternative was for a client to fetch each row's resource when its context menu opens,
                // which is exactly the per-rel round trip ADR 0557 exists to prevent.
                Links = RowLinks(d),
            }).ToList(),
            ContentsSortOrder = folderSortOrder.Value,
            Links = links,
        });
    }

    /// <summary>The addresses a listed row carries — see the call site for which rels belong here and why.</summary>
    private static List<Link> RowLinks(DocumentSummaryRow d)
    {
        var links = new List<Link>
        {
            new("self", $"/api/documents/{d.Id}", "GET"),
            new("chat", $"/api/documents/{d.Id}/chat", "GET"),
            new("versions", $"/api/documents/{d.Id}/versions", "GET"),
            new("children", $"/api/documents/{d.Id}/children", "GET"),
            new("mask", $"/api/documents/{d.Id}/mask", "GET"),
            new("index-data", $"/api/documents/{d.Id}/index-data", "GET"),
            new("references", $"/api/documents/{d.Id}/references", "GET"),
            new("referencing-folders", $"/api/documents/{d.Id}/referencing-folders", "GET"),
        };

        // A notebook and a section hold the same two things, so they advertise the same two creates. Everything
        // else advertises neither, and that absence is what tells a client to leave both off its menu.
        if (WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == d.MaskId) is { } rule
            && rule.Admits.Any(a => a.MaskId == WellKnownMaskIds.Note))
        {
            links.Add(new Link("sections", $"/api/documents/{d.Id}/sections", "POST"));
            links.Add(new Link("notes", $"/api/documents/{d.Id}/notes", "POST"));
        }

        // "New subfolder", gated the same way (#634) — and it must be on the ROW, because that is where both
        // clients' tree nodes get their links (ADR 0555/0557). A rel added only to the single-document GET
        // would leave the menu entry hidden everywhere, since nothing re-fetches a node to populate a menu.
        //
        // parentIsPersonalRoot is false by construction here: a personal space is a ROOT document, so it is
        // never itself a listed child. Its own resource advertises this rel — or withholds it — separately.
        //
        // Unlike the single-document GET this does not check CanCreateSubItems, because a per-row rights
        // resolution is a query per row on the hottest path there is. The mask half of the rule is what this
        // change is for; a caller without the right still meets a 403, exactly as before.
        if (FolderCreationPolicy.AdmitsPlainFolder(d.MaskId, parentIsPersonalRoot: false))
        {
            links.Add(new Link("folders", $"/api/documents/{d.Id}/children", "POST"));
        }

        return links;
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

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    public class CreateChildRequest
    {
        public string Name { get; set; } = "";

        /// <summary>
        /// Which kind of folder to create (#564 slice 2, ADR 0620) — omitted means a plain folder, as before.
        /// Only FOLDER masks can be asked for: what an item is gets decided by classifying its content, not
        /// by a caller asserting it.
        /// </summary>
        public string? FolderMask { get; set; }
    }

    // The folder kinds a caller may name, mapped to their well-known mask. Kept here rather than exposing raw
    // mask ids: the wire stays readable, and the set is exactly the typed folders a client can create.
    private static readonly Dictionary<string, Guid> CreatableFolderMasks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"] = WellKnownMaskIds.Folder,
        ["calendar"] = WellKnownMaskIds.Calendar,
        ["addressbook"] = WellKnownMaskIds.Addressbook,
        ["notebook"] = WellKnownMaskIds.Notebook,
        // "notes" is the name this kind shipped under and stays accepted: the mask was renamed, and a wire
        // value a client may already be sending is not the place to charge for that.
        ["notes"] = WellKnownMaskIds.Notebook,
        ["section"] = WellKnownMaskIds.NotebookSection,
    };

    // The typed-folder family this mask version belongs to as a FOLDER, or null for an ordinary folder. The
    // rules are data (WellKnownMaskIds.TypedFolderRules), so a new typed family needs no change here.
    private async Task<TypedFolderRule?> TypedFolderRuleOfAsync(Guid maskVersionId, CancellationToken cancellationToken)
    {
        var maskId = await _dbContext.MaskVersions
            .Where(v => v.Id == maskVersionId)
            .Select(v => (Guid?)v.MaskId)
            .SingleOrDefaultAsync(cancellationToken);

        return maskId is { } id ? WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == id) : null;
    }

    [HttpPost("children")]
    public async Task<IActionResult> CreateChild(Guid documentId, [FromBody] CreateChildRequest request, CancellationToken cancellationToken)
    {
        var parent = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId, d.MaskVersionId })
            .SingleOrDefaultAsync(cancellationToken);

        if (parent is null)
        {
            return NotFound();
        }

        // Resolve the requested folder kind before doing anything else — an unknown one is the caller's mistake,
        // not a half-created folder.
        var folderMaskId = WellKnownMaskIds.Folder;
        var folderMaskRequested = request.FolderMask is { Length: > 0 };
        if (request.FolderMask is { Length: > 0 } requestedMask
            && !CreatableFolderMasks.TryGetValue(requestedMask, out folderMaskId))
        {
            throw new InvalidFolderMaskException(requestedMask);
        }

        // Is the PARENT a typed folder (Notebook / Section / Addressbook / Calendar)?
        //
        // Two shapes, and the difference is whether the caller SAID what it wants. A named folderMask is an
        // unambiguous ask: honour it when the parent admits that mask — a Section inside a Notebook — and
        // refuse it with the reason when it does not. An unnamed one is ambiguous, because this endpoint
        // serves both "make a folder" and step one of an upload with the same body, so inside a typed folder
        // it is an item-to-be and must be left MASKLESS for the finalizer, exactly as the CardDAV/IMAP write
        // paths leave theirs.
        //
        // Stamping the Folder mask unconditionally is what made an upload into a typed folder impossible: the
        // containment rule exempts a document whose type is not determined yet (its own comment says so, and
        // names .vcf/.ics), and this endpoint was defeating that exemption before the finalizer ever ran.
        var parentRule = parent.MaskVersionId is { } parentMaskVersionId
            ? await TypedFolderRuleOfAsync(parentMaskVersionId, cancellationToken)
            : null;

        var admittedFolder = parentRule is not null && folderMaskRequested
            && parentRule.Admits.Any(a => a.MaskId == folderMaskId);

        if (parentRule is { } rule && folderMaskRequested && !admittedFolder)
        {
            throw new Errors.Exceptions.Documents.TypedFolderContainmentException(
                $"A {rule.FolderName} holds only {rule.AdmittedNames} — '{request.FolderMask}' cannot live there.");
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanCreateSubItems)
        {
            return Forbid();
        }

        var (createdByUserId, createdByServiceAccountId) = _access.GetCallerIdentity();

        var child = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = parent.TenantId,
            ParentId = documentId,
            Name = request.Name,
            // Assigned the Folder mask now; if a version is later added, finalize reclassifies it (ADR "Folder mask on folders").
            // A typed folder (#564) instead wears the mask the request named — resolved tenant-explicitly, so a
            // caller with no ambient tenant can't produce a maskless folder (ADR 0590's defect).
            // Inside a typed folder, null UNLESS the caller named a mask that folder admits (a Section in a
            // Notebook): an unnamed one is an item-to-be, and the finalizer decides what it is (see above).
            MaskVersionId = parentRule is not null && !admittedFolder
                ? null
                : await Documents.FolderMask.CurrentVersionIdAsync(_dbContext, parent.TenantId, folderMaskId, cancellationToken)
                    ?? await Documents.FolderMask.CurrentVersionIdAsync(_dbContext, cancellationToken),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(child);

        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw DocumentNameConflictException.OnSameParent();
        }

        await _queue.EnqueueAsync(child.Id, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentCreated, "Document", child.Id, child.Name, cancellationToken: cancellationToken);

        // `versions` alongside `self` because creating a child is step one of a THREE-step upload (create,
        // add a version, finalize), and a create response that hands back only an id is precisely what forces
        // the next two steps to be composed from it (ADR 0543, issue #416).
        var resource = new DocumentSummaryResource
        {
            Id = child.Id,
            Name = child.Name,
            Links =
            [
                new Link("self", $"/api/documents/{child.Id}", "GET"),
                new Link("versions", $"/api/documents/{child.Id}/versions", "GET"),
            ],
        };

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId = child.Id }, resource);
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

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
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
        if (realParentId is { } parentId && await _access.CanSeeAsync(parentId, cancellationToken))
        {
            var parentName = await _dbContext.Documents
                .Where(d => d.Id == parentId)
                .Select(d => d.Name)
                .SingleAsync(cancellationToken);

            primaryLocation = new PrimaryLocationResource
            {
                Id = parentId,
                Name = parentName,
                Path = await _dbContext.BuildFolderPathAsync(parentId, cancellationToken),
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
                if (await _access.CanSeeAsync(candidate.FolderId, cancellationToken))
                {
                    hasMore = true;
                    break;
                }

                lastCreatedAt = candidate.CreatedAt;
                lastId = candidate.ReferenceId;
                continue;
            }

            if (await _access.CanSeeAsync(candidate.FolderId, cancellationToken))
            {
                visible.Add(new ReferencingFolderResource
                {
                    Id = candidate.FolderId,
                    Name = candidate.FolderName,
                    Path = await _dbContext.BuildFolderPathAsync(candidate.FolderId, cancellationToken),
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

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }
}
