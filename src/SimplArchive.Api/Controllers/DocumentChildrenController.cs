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
using SimplArchive.Infrastructure.Masks;
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

    // Shared with the SaveChanges invariant, so what this offers and what that permits are the same answer
    // rather than two that agree today (#673, ADR 0655).
    private readonly IMaskContainmentProvider _containment;

    public DocumentChildrenController(
        ICurrentUserAccessor currentUserAccessor,
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        IAuditRecorder audit,
        IDocumentIndexQueue queue,
        Documents.IClearanceScopeResolver clearanceScope,
        ICurrentTenantAccessor currentTenantAccessor,
        IMaskContainmentProvider containment)
    {
        _currentUserAccessor = currentUserAccessor;
        _dbContext = dbContext;
        _access = access;
        _audit = audit;
        _queue = queue;
        _clearanceScope = clearanceScope;
        _currentTenantAccessor = currentTenantAccessor;
        _containment = containment;
    }

    public class DocumentSummaryResource : HypermediaResource, Hypermedia.ICarriesRowCapabilities
    {
        /// <summary>May the caller DELETE this row? (#858)</summary>
        /// <remarks>
        /// A flag, not a rel: <c>DELETE</c> is at this item's own address, so a <c>delete</c> rel beside
        /// <c>self</c> would be the same URL under a second name (ADR 0719). Absence means the same as a
        /// missing rel — not available to you, here, now (ADR 0543) — so a client disables Delete rather than
        /// offering it and handling a 403.
        /// </remarks>
        public bool CanDelete { get; set; }

        /// <summary>May the caller rename this row, or change its index data or contents order? (#858)</summary>
        /// <remarks>Named for the RIGHT the <c>PUT</c> enforces, so the gate and the refusal cannot drift.</remarks>
        public bool CanEditIndexData { get; set; }

        /// <summary>May this item be moved? (#858)</summary>
        /// <remarks>
        /// Says the ITEM may be moved, never that a given move will succeed: a move also needs
        /// <c>CanCreateSubItems</c> on the TARGET, which no row can answer before a target is chosen — the
        /// picker owns that half (ADR 0689).
        /// </remarks>
        public bool CanMove { get; set; }

        /// <summary>May the caller manage this row's permissions? (#858)</summary>
        public bool CanManagePermissions { get; set; }

        /// <summary>May the caller create a plain child inside this row? (#854)</summary>
        /// <remarks>Policy AND right — see <c>ICarriesRowCapabilities.CanCreateChildren</c>.</remarks>
        public bool CanCreateChildren { get; set; }


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

        // Who filed the current version, falling back to who created the document (#768). A NAME, not an id:
        // the column is read by a person, and a listing that returned an id would make every client fetch the
        // user per row — one request per row, which is what ADR 0557 exists to prevent.
        public string CreatedBy { get; set; } = "";

        /// <summary>
        /// The kinds of child this folder will accept, each with the address that creates one (#673).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Inline rather than behind a rel, because a context menu opens on a right-click and a round trip
        /// there is a visible pause on the one interaction that must feel instant. It is a value that already
        /// travels with the listing, which is exactly where ADR 0557 says to take it from.
        /// </para>
        /// <para>
        /// Empty for anything that is not a folder, and for a folder that accepts nothing — and that emptiness
        /// is meaningful in the same way a missing rel is (ADR 0543): the client shows no "New …" entries
        /// rather than offering ones the server would refuse.
        /// </para>
        /// </remarks>
        public List<CreatableChild> Admits { get; set; } = [];

        /// <summary>What to DRAW for this row — a token from the mask, or null for the shape default.</summary>
        /// <remarks>
        /// A token rather than an icon name, because the two clients draw from different icon sets. Null and
        /// unrecognised both mean "use the folder/document glyph you always used", so a row is never worse off
        /// than before this existed.
        /// </remarks>
        public string? Icon { get; set; }

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

    /// <summary>The listed folder itself — its sort order, and what the `create-child` rel needs to decide.</summary>
    private record FolderRow(FolderContentsSortOrder ContentsSortOrder, Guid? MaskId, bool IsPersonalRoot);


    public class DocumentChildrenResource : HypermediaResource
    {
        public List<DocumentSummaryResource> Children { get; set; } = [];

        // The listed folder's persisted default contents sort order (ADR "Per-folder contents sort order"). The
        // clients apply it (folders-first) as the default order when the folder is opened; a column-header click
        // is an ephemeral override. Serialized as the int enum value (Name=0/DocumentDate=1/Created=2).
        public FolderContentsSortOrder ContentsSortOrder { get; set; }

        /// <summary>May the caller create a plain child in the folder this collection belongs to? (#854)</summary>
        /// <remarks>
        /// The collection's own `create-child` rel, converted: it pointed at this very collection's address and
        /// differed from `self` only by method (ADR 0719). The reason it exists at all is the one its removed
        /// comment gave — a caller that reached a folder through its collection rather than through its
        /// parent's listing must get the SAME answer to "can I create here?" as the row would give (ADR 0637).
        /// </remarks>
        public bool CanCreateChildren { get; set; }
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
        // Also reads what the `create-child` rel below needs — the folder's own mask, and whether it is a
        // personal root. In this query rather than a second one: it already reads this row, and the rel is
        // about THIS folder, not about the children.
        var folder = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new FolderRow(
                d.ContentsSortOrder,
                _dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault(),
                d.PersonalOfUserId != null))
            .FirstOrDefaultAsync(cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        var folderSortOrder = folder.ContentsSortOrder;

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
            .AsSummaryRows(_dbContext)
            .ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        // Tags per row (ADR "List-row columns and sorting") — a single batched query over the page's ids.
        var tagsByDoc = await DocumentSummaryQueries.TagsForAsync(_dbContext, page.Select(p => p.Id).ToList(), cancellationToken);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(ListChildren), new { documentId, cursor, limit = pageSize })!, "GET"),
        };

        // `create-child` was advertised here UNCONDITIONALLY, which is the same bug the row-level rel was added
        // to fix, one level up: it offered a create on a Notebook, on an ephemeral staging folder and on a
        // personal space's first level, all of which the server refuses (#634).
        //
        // It matters more than a stray link, because this rel and the row's are now ONE name. A caller that
        // reached the same folder through its collection rather than through its parent's listing would
        // otherwise get the opposite answer to the same question — which is precisely the drift that having one
        // rel for one create is meant to remove (ADR 0637).

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(ListChildren), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        // Loaded ONCE for the whole page, not per row: the provider caches per request, so this is one query
        // however many rows the page holds — and the same object the invariant will consult if any of these
        // creates is actually attempted.
        var rules = await _containment.ForAsync(_dbContext, _currentTenantAccessor.TenantId!.Value, cancellationToken);

        var children = page.Select(d => new DocumentSummaryResource
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
            CreatedBy = d.CreatedByName ?? "",
            // isPersonalRoot is false by construction: a personal space is a ROOT document, so it is never
            // itself a listed child. Its own resource answers this separately.
            Admits = CreatableChildren.For(rules, d.Id, d.MaskId, isPersonalRoot: false),
            // From the rules object already loaded for this page — the mask facts are all in one place, so
            // the icon costs no query.
            Icon = rules.IconOf(d.MaskId),
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
            // resource computes once, so those affordances still require the resource itself, and their
            // absence here must NOT be read as "not available" (ADR 0543's rule applies to a resource, not
            // to a summary).
            //
            // That used to be argued as "a listing is the wrong place to answer 'may I?'", and the premise
            // has since changed rather than the conclusion: the objection was COST — a rights resolution
            // per row — and GetCallerRightsForManyAsync now answers a whole page in about the queries one
            // document took. So the three rights a destructive menu needs ARE answered here, as the
            // CanDelete/CanEditIndexData/CanMove flags above (#858). What stays out is the rest, which no
            // menu gates on, and the distinction to hold is between a flag (a yes/no this listing can
            // afford) and a rel (an ADDRESS, which a row should not multiply).
            //
            // A MASK-DERIVED rel is not one of those, and the distinction matters (#564). Whether a folder
            // holds sections and notes is a property of its mask, not of who is asking — and the query is
            // already joining MaskVersions to produce DocumentType, so it costs nothing to answer here.
            // The alternative was for a client to fetch each row's resource when its context menu opens,
            // which is exactly the per-rel round trip ADR 0557 exists to prevent.
            Links = RowLinks(d),
        }).ToList();

        // The per-row rights, in ONE batch for the page (#858) — this is what lets a client gate Delete/Rename/
        // Move honestly instead of offering them to anyone who can see a row and answering with a 403.
        //
        // The mask half of CanCreateChildren is read from `page`, where the mask is in scope — the row DTO does
        // not carry it, and adding it purely to answer this would put a column on the wire for the client to
        // re-derive a rule the server already knows (#854). parentIsPersonalRoot is false by construction: a
        // personal space is a ROOT document, so it is never itself a listed child.
        var admitsPlainChild = page.ToDictionary(
            d => d.Id,
            d => ChildCreationPolicy.AdmitsPlainChild(d.MaskId, parentIsPersonalRoot: false));

        await Hypermedia.RowCapabilities.StampAsync(
            children, r => r.Id, r => admitsPlainChild[r.Id], _access, cancellationToken);

        // The FOLDER's own rights, which the per-row batch above does not cover — it answers the children, and
        // this answers their parent. One document's worth of work on a read that already does several queries,
        // and the alternative (a client fetching the folder resource just to learn whether it may upload here)
        // is the per-rel round trip ADR 0557 exists to prevent.
        var folderRights = await _access.GetCallerRightsAsync(documentId, cancellationToken);

        return Ok(new DocumentChildrenResource
        {
            Children = children,
            ContentsSortOrder = folderSortOrder,
            CanCreateChildren = folderRights.CanCreateSubItems
                && ChildCreationPolicy.AdmitsPlainChild(folder.MaskId, folder.IsPersonalRoot),
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

        // …and the same for the other two typed families (#631). Keyed on the ADMITTED item mask rather than on
        // the folder's, so the rule reads the same way for all three and a new family is a row in the table
        // rather than another branch here.
        if (ChildCreationPolicy.AdmitsTypedItem(d.MaskId, WellKnownMaskIds.Contact))
        {
            links.Add(new Link("contacts", $"/api/documents/{d.Id}/contacts", "POST"));
        }

        if (ChildCreationPolicy.AdmitsTypedItem(d.MaskId, WellKnownMaskIds.Appointment))
        {
            links.Add(new Link("appointments", $"/api/documents/{d.Id}/appointments", "POST"));
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

        return links;
    }

    // Batched tag lookup for a page of documents (ADR "List-row columns and sorting") — one query, grouped by
    // document, tags sorted; empty for a document with none.
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

        /// <summary>
        /// The mask to create, as the <c>admits</c> entry gave it (#678) — the general form of
        /// <see cref="FolderMask"/>, which only names the handful of kinds that ever had a slug.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An id on the wire where this API has otherwise exposed vocabulary, and taken deliberately: the
        /// client is <b>echoing back what the server gave it</b> on the row, exactly as it does with
        /// <c>folderMask</c>, so it composes nothing and keeps no copy of the mask set (ADR 0543). The
        /// alternative was a stable per-mask slug column, which needs uniqueness rules, a migration and an
        /// answer for the existing one-to-many alias — "notes" and "notebook" both mean Notebook, which a slug
        /// column cannot say.
        /// </para>
        /// <para>
        /// Wins over <see cref="FolderMask"/> when both are sent. A client that knows about this field is
        /// newer than one that only knows slugs, and the two can only disagree if something built the body by
        /// hand.
        /// </para>
        /// </remarks>
        public Guid? MaskId { get; set; }
    }

    // The folder kinds a caller may name by SLUG. No longer the gate — whether a mask may be created is
    // Mask.UserCreatable and whether it may live here is containment, both data (#678) — this is now a
    // compatibility alias table so a client built before that keeps working, and so the wire stays readable
    // for the kinds that always had a name.
    //
    // It cannot grow to cover a tenant-authored mask, which is exactly why MaskId exists beside it.
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

        // Resolve the requested kind before doing anything else — an unknown one is the caller's mistake,
        // not a half-created folder.
        //
        // MaskId wins over FolderMask: it is the general form, and a client sending it is newer than one
        // sending only a slug. Both come from the `admits` entry the server handed out, so they can disagree
        // only if something assembled the body by hand.
        var rules = await _containment.ForAsync(_dbContext, parent.TenantId, cancellationToken);
        var folderMaskId = WellKnownMaskIds.Folder;
        var folderMaskRequested = request.MaskId is not null || request.FolderMask is { Length: > 0 };

        if (request.MaskId is { } requestedMaskId)
        {
            // Gated on the DATA, not on a table of names (#678). A mask this tenant does not have, or one
            // provisioning owns, is refused here — which is what stops a caller asking for a Mailbox or a
            // Repository by id now that ids are on the wire. Containment is asked separately below, so the
            // refusal a caller gets names the right reason.
            if (!rules.IsUserCreatable(requestedMaskId) || !rules.IsFolderMask(requestedMaskId))
            {
                throw new InvalidFolderMaskException(requestedMaskId.ToString());
            }

            folderMaskId = requestedMaskId;
        }
        else if (request.FolderMask is { Length: > 0 } requestedMask
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
