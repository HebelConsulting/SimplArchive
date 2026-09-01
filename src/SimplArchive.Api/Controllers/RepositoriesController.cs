using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The Repositories entrypoint (ADR "Planned API architecture") — the root document (see
/// RootController) links here. See ADR "Repositories controller and Document creation", ADR
/// "Repository/Document unification". A "repository" is now just a Document with ParentId == null — this
/// controller is a thin semantic layer over DocumentsController's own operations, not a separate entity,
/// preserving the useful "repository" vocabulary for API consumers. List/ListDocuments/ListRecycleBin are
/// paginated — see ADR "Pagination for list endpoints". List is the one endpoint in the whole Api that
/// filters per-item (CanSee on each independent root document) rather than checking one right once, so its
/// pagination works differently from every other list endpoint — see the comment on List below.
/// Authorization checks accept either a ServiceAccount or a logged-in User caller — see ADR
/// "Document-scope authorization retrofit for User, and tenant-administrator-driven onboarding".
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/repositories")]
[Authorize]
public class RepositoriesController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly SimplArchive.Infrastructure.Masks.IMaskContainmentProvider _containment;

    public RepositoriesController(
        SimplArchiveDbContext dbContext,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IUserSystemRightsResolver userSystemRights,
        IDocumentIndexQueue queue,
        IAuditRecorder audit,
        Documents.DocumentPurger purger,
        Documents.RepositoryImporter importer,
        Documents.IClearanceScopeResolver clearanceScope,
        SimplArchive.Infrastructure.Masks.IMaskContainmentProvider containment,
        Documents.DocumentAccessService access)
    {
        _dbContext = dbContext;
        _clearanceScope = clearanceScope;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _userSystemRights = userSystemRights;
        _queue = queue;
        _audit = audit;
        _purger = purger;
        _importer = importer;
        _containment = containment;
        _access = access;
    }

    private readonly IDocumentIndexQueue _queue;
    private readonly IAuditRecorder _audit;
    private readonly Documents.IClearanceScopeResolver _clearanceScope;
    private readonly Documents.DocumentPurger _purger;
    private readonly Documents.RepositoryImporter _importer;

    // The shared caller-access service (ADR 0571). This controller still has its own private
    // GetCallerRightsAsync below — one of the copies the extraction set out to remove and this one did not
    // reach — so the batch is taken from the shared service rather than growing a SECOND local copy beside it.
    private readonly Documents.DocumentAccessService _access;

    public class RepositoryResource : HypermediaResource, Hypermedia.ICarriesRowCapabilities
    {
        /// <summary>May the caller DELETE this repository? (#858)</summary>
        /// <remarks>
        /// A tree ROOT is where the destructive gap was most visible: both clients offered Delete, Rename and
        /// Move on a repository's context menu with no gate at all. Flags rather than rels for ADR 0719's
        /// reason — DELETE and PUT are at the item's own address.
        /// </remarks>
        public bool CanDelete { get; set; }

        /// <summary>May the caller rename this repository or change its index data / contents order? (#858)</summary>
        public bool CanEditIndexData { get; set; }

        /// <summary>May this repository be moved (i.e. DEMOTED under a folder)? (#858)</summary>
        /// <remarks>
        /// For a root this is the demotion case, which needs the CanManageRepositories system right on top of
        /// CanMove — so it is emphatically not "has a parent". The row reports CanMove alone; the system right
        /// is already in the client's hands from whoami, and the server enforces both.
        /// </remarks>
        public bool CanMove { get; set; }

        /// <summary>May the caller manage this row's permissions? (#858)</summary>
        public bool CanManagePermissions { get; set; }

        /// <summary>May the caller create a plain child inside this row? (#854)</summary>
        /// <remarks>Policy AND right — see <c>ICarriesRowCapabilities.CanCreateChildren</c>.</remarks>
        public bool CanCreateChildren { get; set; }


        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        // Presentation metadata for the browse tree (ADR "Blazor repository/document browsing"):
        // HasChildren governs whether the contents list can drill in; HasVersions picks the icon;
        // HasSubfolders (a child that is itself a folder — no versions) governs the folder tree's expand
        // caret, since the tree shows only folders (ADR "Workbench pane content fixes"). Computed, never
        // stored.
        public bool HasChildren { get; set; }

        public bool HasVersions { get; set; }

        public bool HasSubfolders { get; set; }

        /// <summary>What a client may create in this repository, with the address for each (#673).</summary>
        /// <remarks>
        /// On the ROW for the same reason `create-child` is, and stated one paragraph below: a tree's top-level
        /// nodes get their whole menu from this listing and nothing re-fetches a node to fill one in. Carried
        /// here and on the children listing — the two places a folder is ever LISTED — so every folder in the
        /// tree answers the question, not just the ones somebody drilled into.
        /// </remarks>
        public List<Documents.CreatableChild> Admits { get; set; } = [];

        /// <summary>What to draw for this repository — a token from its mask (see the children listing).</summary>
        public string? Icon { get; set; }
    }

    private record RepositoryRow(Guid Id, string Name, DateTimeOffset CreatedAt, bool HasChildren, bool HasVersions, bool HasSubfolders, Guid? MaskId);

    public class RepositoryListResource : HypermediaResource
    {
        public List<RepositoryResource> Repositories { get; set; } = [];
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Unlike every
    // other list endpoint, this one filters per-item (CanSee on each independent root document — there's
    // no single parent to check once, unlike ListChildren/ListDocuments), so clean keyset pagination
    // doesn't compose with a straight Take(limit + 1): the database can't express "the Nth row this
    // caller can see" in one query. Instead this walks candidates in cursor order, collecting visible
    // ones until the page is full, then keeps scanning — without collecting — only as far as needed to
    // prove at least one more visible document exists beyond the page (stopping as soon as it finds one).
    // The next cursor is derived from whichever candidate (visible or not) was last examined, so the next
    // page resumes from exactly the right position. No less efficient than before this ADR — List was
    // already O(all root documents); this only changes the response contract to expose a cursor instead
    // of returning everything at once.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var pageSize = PageSize.Resolve(limit);

        // Once for the page, never once per row (#673): the rules are a tenant-wide fact and every row asks the
        // same question of them.
        var rules = await _containment.ForAsync(_dbContext, _currentTenantAccessor.TenantId!.Value, cancellationToken);

        // Personal repositories (ADR "Per-user personal repository") are surfaced separately (the clients' "Personal"
        // node) — keep them out of the shared repository list.
        var query = _dbContext.Documents.Where(d => d.ParentId == null && d.PersonalOfUserId == null);

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(d => d.CreatedAt > cursorCreatedAt || (d.CreatedAt == cursorCreatedAt && d.Id > cursorId));
        }

        // Filter/order on the entity, then project — HasChildren/HasVersions computed in SQL (two .Any()
        // subqueries per row), never stored. See ADR "Blazor repository/document browsing". A root document
        // ("repository") can hold children like any other.
        var candidates = await query
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Select(d => new RepositoryRow(
                d.Id,
                d.Name,
                d.CreatedAt,
                // Child document/subfolder OR a reference filed into it (issue #376).
                _dbContext.Documents.Any(c => c.ParentId == d.Id)
                    || _dbContext.DocumentReferences.Any(x => x.ParentFolderId == d.Id),
                _dbContext.DocumentVersions.Any(v => v.DocumentId == d.Id),
                _dbContext.Documents.Any(c => c.ParentId == d.Id && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id)),
                // The mask, for the `folders` rel below — asked per row rather than assumed, so this listing
                // answers the question with the same predicate every other surface does.
                _dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        // Every candidate's rights in ONE batch (#858), replacing a CanSeeAsync per row.
        //
        // This endpoint is the per-item-ACL-filtered exception (ADR "Pagination for list endpoints"), and the
        // candidate set above is deliberately UNBOUNDED — it has to walk until the page fills, because whether
        // a row is visible cannot be expressed in the query. So the old shape cost one rights resolution per
        // root document in the TENANT for a caller who can see few of them, each ~5 queries plus one per
        // ancestor level. Batching makes the whole walk cost about what a single document did, and it is what
        // makes the three capability flags below free rather than a reason not to emit them.
        var candidateRights = await _access.GetCallerRightsForManyAsync([.. candidates.Select(c => c.Id)], cancellationToken);

        var visible = new List<RepositoryResource>();
        DateTimeOffset? lastExaminedCreatedAt = null;
        Guid? lastExaminedId = null;
        var hasMore = false;

        foreach (var candidate in candidates)
        {
            if (visible.Count >= pageSize)
            {
                if (candidateRights[candidate.Id].CanSee)
                {
                    hasMore = true;
                    break;
                }

                lastExaminedCreatedAt = candidate.CreatedAt;
                lastExaminedId = candidate.Id;

                continue;
            }

            if (candidateRights[candidate.Id].CanSee)
            {
                // "New subfolder" on a repository root (#634). It belongs on the ROW because that is where both
                // clients' top-level tree nodes get their links (ADR 0555/0557) — a rel that lived only on the
                // single-repository GET would leave the menu entry hidden on every repository, since nothing
                // re-fetches a tree node to populate its menu. That is exactly what adding the rel to the
                // children listing and to GET /documents/{id} — but not here — did.
                //
                // parentIsPersonalRoot is false by construction: this listing excludes personal repositories
                // (see the query above), and PersonalRepositoryController deliberately withholds the rel.
                // Mask-only, as on the children listing: a caller without CanCreateSubItems still meets a 403.
                // (This used to add "per-row rights beyond that is a query per row" — no longer true, and it was
                // the objection that kept the destructive affordances ungated. See the batch above.)
                //
                // A repository root is where the tree starts, so its row also carries the address of what a
                // client does next — list its children. Without it the client has an id and no address and
                // composes the path (ADR 0543, issue #416); fetching `self` first would cost a round trip
                // per row. Otherwise unconditional rels only, as on the children listing: checkout/external-
                // links/acl-inheritance are answered by the document resource, so their absence here does NOT
                // mean "not available". The three rights a destructive menu needs ARE answered, as the
                // CanDelete/CanEditIndexData/CanMove flags — a flag is a yes/no this listing can now afford,
                // where a rel is an address a row should not multiply (#858).
                var rowLinks = new List<Link>
                {
                        new Link("self", Url.Action(nameof(Get), new { repositoryId = candidate.Id })!, "GET"),
                        // The SAME object seen as a document (ADR 0200 — a repository IS a document with no
                        // parent). `self` here is the repository view, which answers different questions and
                        // carries different rels; anything that needs the document view — its grants, its
                        // sensitivity, its check-out state — must follow this instead. Without it a client
                        // holding a repository row has no address for the document at all, and the manage-access
                        // dialog read the repository resource, found no `acl-entries`, and told the user it had
                        // no permission (issue #416).
                        new Link("document", $"/api/documents/{candidate.Id}", "GET"),
                        new Link("children", $"/api/documents/{candidate.Id}/children", "GET"),
                        // Opening a repository lists its children AND the shortcuts filed in it, exactly as
                        // opening any other folder does — so the row must carry both, or a client following
                        // rels has to fall back to fetching the resource on the one node it starts from.
                        new Link("references", $"/api/documents/{candidate.Id}/references", "GET"),
                        new Link("recycle-bin", $"/api/repositories/{candidate.Id}/recycle-bin", "GET"),
                        new Link("index-data", $"/api/documents/{candidate.Id}/index-data", "GET"),
                        new Link("mask", $"/api/documents/{candidate.Id}/mask", "GET"),
                };

                var rights = candidateRights[candidate.Id];

                visible.Add(new RepositoryResource
                {
                    Id = candidate.Id,
                    Name = candidate.Name,
                    CanDelete = rights.CanDelete,
                    CanEditIndexData = rights.CanEditIndexData,
                    CanMove = rights.CanMove,
                    CanManagePermissions = rights.CanManagePermissions,
                    // Replaces the `create-child` rel this row used to add beside `children` — one address
                    // under two names, with the method already saying which action it is (#854, ADR 0719).
                    // The rel answered the mask half only; the flag answers both, and the rights are already
                    // in hand here.
                    CanCreateChildren = ChildCreationPolicy.AdmitsPlainChild(candidate.MaskId, parentIsPersonalRoot: false)
                        && rights.CanCreateSubItems,
                    HasChildren = candidate.HasChildren,
                    HasVersions = candidate.HasVersions,
                    HasSubfolders = candidate.HasSubfolders,
                    Links = rowLinks,
                    Admits = Documents.CreatableChildren.For(rules, candidate.Id, candidate.MaskId, isPersonalRoot: false),
                    Icon = rules.IconOf(candidate.MaskId),
                });
            }

            lastExaminedCreatedAt = candidate.CreatedAt;
            lastExaminedId = candidate.Id;
        }

        // Import an export archive as a brand-new top-level repository. It belongs to the COLLECTION, not to any
        // repository in it — the archive's root becomes a new sibling — which is exactly why the client had
        // nowhere to follow from and composed the path instead (issue #416). Static like the document-level
        // `import`: the CanImport system right is already in the client's hands from whoami.
        var links = new List<Link>
        {
            new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET"),
            new("import", Url.Action(nameof(Import))!, "POST"),
        };

        if (hasMore && lastExaminedCreatedAt is { } lastCreatedAt && lastExaminedId is { } lastId)
        {
            var nextCursor = Cursor.Encode(lastCreatedAt, lastId);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new RepositoryListResource
        {
            Repositories = visible,
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically. List itself never 404s/403s (it filters
    // per-item), so this mirrors that: unconditional 204.
    [HttpHead]
    public IActionResult HeadList()
    {
        return NoContent();
    }

    public class CreateRepositoryRequest
    {
        public string Name { get; set; } = "";
    }

    // Requires ServiceAccount.CanManageRepositories or User.CanManageRepositories — a dedicated right
    // (ADR 0136), not an ACL check, since there's no document yet to scope a grant to. The creating
    // principal gets an auto-granted, full-rights AclEntry on the new root document — a ServiceAccount has
    // no IsTenantAdmin-equivalent bypass (ADR 0181), so without this it would have zero access to what it
    // just created (a User with IsTenantAdmin would already see it regardless, but gets the same explicit
    // grant anyway for consistency). See ADR "Repository creation endpoint", ADR
    // "Repository/Document unification", ADR "Document-scope authorization retrofit for User, and
    // tenant-administrator-driven onboarding".
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryRequest request, CancellationToken cancellationToken)
    {
        Guid? createdByUserId = null;
        Guid? createdByServiceAccountId = null;

        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            var canManageRepositories = await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanManageRepositories)
                .SingleAsync(cancellationToken);

            if (!canManageRepositories)
            {
                return Forbid();
            }

            createdByServiceAccountId = serviceAccountId;
        }
        else if (_currentUserAccessor.UserId is { } userId)
        {
            // Effective rights (own ∪ groups) — ADR "Enforce group system rights for members".
            var canManageRepositories = (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageRepositories;

            if (!canManageRepositories)
            {
                return Forbid();
            }

            createdByUserId = userId;
        }
        else
        {
            return Forbid();
        }

        var tenantId = _currentTenantAccessor.TenantId!.Value;

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = null,
            Name = request.Name,
            // A repository wears the Repository mask, kept in lockstep with ParentId == null (ADR 0627). It was
            // the plain Folder mask, which made a repository indistinguishable from any folder in a listing or
            // over IMAP without a second query.
            MaskVersionId = await Documents.FolderMask.CurrentVersionIdAsync(
                _dbContext, tenantId, SimplArchive.Domain.Masks.WellKnownMaskIds.Repository, cancellationToken),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(document);

        _dbContext.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = document.Id,
            UserId = createdByUserId,
            ServiceAccountId = createdByServiceAccountId,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanDelete = true,
            CanCreateSubItems = true,
            CanManagePermissions = true,
            CanMove = true,
            CanAnnotate = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Document's own sibling-name-uniqueness check (SimplArchiveDbContext.SaveChanges) throws
            // this for a name collision among root-level documents — the same tenant-wide scope
            // Repository.Name's own uniqueness used to enforce via a DB constraint (ADR 0154), now
            // naturally reproduced by the unified Document check (ADR "Repository/Document unification").
            throw DocumentNameConflictException.OnSameParent();
        }

        await _queue.EnqueueAsync(document.Id, cancellationToken);
        await _audit.RecordAsync(AuditActions.RepositoryCreated, "Document", document.Id, document.Name, cancellationToken: cancellationToken);

        var resource = new RepositoryResource
        {
            Id = document.Id,
            Name = document.Name,
            Links = [new Link("self", Url.Action(nameof(Get), new { repositoryId = document.Id })!, "GET")],
        };

        return CreatedAtAction(nameof(Get), new { repositoryId = document.Id }, resource);
    }

    [HttpGet("{repositoryId:guid}")]
    public async Task<IActionResult> Get(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repository = await _dbContext.Documents
            .Where(d => d.Id == repositoryId && d.ParentId == null)
            .Select(d => new
            {
                d.Id,
                d.Name,
                // Child document/subfolder OR a reference filed into it (issue #376).
                HasChildren = _dbContext.Documents.Any(c => c.ParentId == d.Id)
                    || _dbContext.DocumentReferences.Any(x => x.ParentFolderId == d.Id),
                HasVersions = _dbContext.DocumentVersions.Any(v => v.DocumentId == d.Id),
                HasSubfolders = _dbContext.Documents.Any(c => c.ParentId == d.Id && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id)),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (repository is null)
        {
            return NotFound();
        }

        if (!await CanSeeAsync(repositoryId, cancellationToken))
        {
            return Forbid();
        }

        return Ok(new RepositoryResource
        {
            Id = repository.Id,
            Name = repository.Name,
            HasChildren = repository.HasChildren,
            HasVersions = repository.HasVersions,
            HasSubfolders = repository.HasSubfolders,
            Links =
            [
                new Link("self", Url.Action(nameof(Get), new { repositoryId })!, "GET"),
                new Link("documents", Url.Action(nameof(ListDocuments), new { repositoryId })!, "GET"),
                new Link("recycle-bin", Url.Action(nameof(ListRecycleBin), new { repositoryId })!, "GET"),
            ],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{repositoryId:guid}")]
    public async Task<IActionResult> HeadGet(Guid repositoryId, CancellationToken cancellationToken)
    {
        if (!await RepositoryExistsAsync(repositoryId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(repositoryId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
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

        // Presentation metadata for the browse tree (ADR "Blazor repository/document browsing").
        public bool HasChildren { get; set; }

        public bool HasVersions { get; set; }

        // True when at least one DocumentReference targets this item — see ADR "References-of-an-item list".
        public bool HasReferences { get; set; }

        // True when directly in an active legal hold (ADR "Legal hold & retention enforcement") — lock icon.
        public bool OnLegalHold { get; set; }
    }

    private record DocumentSummaryRow(Guid Id, string Name, DateTimeOffset CreatedAt, bool HasChildren, bool HasVersions, bool HasReferences, bool OnLegalHold, Guid? MaskId);

    public class DocumentListResource : HypermediaResource
    {
        public List<DocumentSummaryResource> Documents { get; set; } = [];
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet("{repositoryId:guid}/documents")]
    public async Task<IActionResult> ListDocuments(Guid repositoryId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await RepositoryExistsAsync(repositoryId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(repositoryId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        // Clearance enforcement (ADR "Sensitivity clearance enforcement") — hide over-clearance documents.
        var clearance = await _clearanceScope.ResolveAsync(cancellationToken);
        var query = clearance.Filter(_dbContext.Documents.Where(d => d.ParentId == repositoryId));

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(d => d.CreatedAt > cursorCreatedAt || (d.CreatedAt == cursorCreatedAt && d.Id > cursorId));
        }

        var fetched = await query
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Take(pageSize + 1)
            .Select(d => new DocumentSummaryRow(
                d.Id,
                d.Name,
                d.CreatedAt,
                // Child document/subfolder OR a reference filed into it (issue #376).
                _dbContext.Documents.Any(c => c.ParentId == d.Id)
                    || _dbContext.DocumentReferences.Any(x => x.ParentFolderId == d.Id),
                _dbContext.DocumentVersions.Any(v => v.DocumentId == d.Id),
                _dbContext.DocumentReferences.Any(r => r.TargetDocumentId == d.Id),
                _dbContext.LegalHoldItems.Any(i => i.DocumentId == d.Id && _dbContext.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null)),
                // The document's MASK, for CanCreateChildren's mask half (#889) — the rights half comes from
                // the batch below. Resolved through MaskVersion the same way the children listing does it;
                // Document itself carries only MaskVersionId.
                _dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(ListDocuments), new { repositoryId, cursor, limit = pageSize })!, "GET"),
            new("create-document", Url.Action(nameof(CreateDocument), new { repositoryId })!, "POST"),
        };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(ListDocuments), new { repositoryId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        var documents = page.Select(d => new DocumentSummaryResource
        {
            Id = d.Id,
            Name = d.Name,
            HasChildren = d.HasChildren,
            HasVersions = d.HasVersions,
            HasReferences = d.HasReferences,
            OnLegalHold = d.OnLegalHold,
            Links = new List<Link> { new("self", $"/api/documents/{d.Id}", "GET") },
        }).ToList();

        // These rows implement ICarriesRowCapabilities and were never stamped (#889), so every one of them
        // reported canDelete/canMove/canEditIndexData/canManagePermissions/canCreateChildren as FALSE. Nothing
        // broke visibly because no client follows this listing's `documents` rel yet — but a conforming client
        // that did would have been told it may do nothing at all with rows it owns, which is precisely the
        // promise ADR 0543 exists to keep. The interface makes DECLARING the flags a compile error; it cannot
        // make POPULATING them one, and this was the first place that difference bit.
        var admitsPlainChild = page.ToDictionary(
            d => d.Id,
            d => ChildCreationPolicy.AdmitsPlainChild(d.MaskId, parentIsPersonalRoot: false));

        await Hypermedia.RowCapabilities.StampAsync(
            documents, r => r.Id, r => admitsPlainChild[r.Id], _access, cancellationToken);

        return Ok(new DocumentListResource
        {
            Documents = documents,
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{repositoryId:guid}/documents")]
    public async Task<IActionResult> HeadListDocuments(Guid repositoryId, CancellationToken cancellationToken)
    {
        if (!await RepositoryExistsAsync(repositoryId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(repositoryId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    public class CreateDocumentRequest
    {
        public string Name { get; set; } = "";
    }

    [HttpPost("{repositoryId:guid}/documents")]
    public async Task<IActionResult> CreateDocument(Guid repositoryId, [FromBody] CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        var parent = await _dbContext.Documents
            .Where(d => d.Id == repositoryId && d.ParentId == null)
            .Select(d => new { d.TenantId })
            .SingleOrDefaultAsync(cancellationToken);

        if (parent is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(repositoryId, cancellationToken);

        if (!rights.CanCreateSubItems)
        {
            return Forbid();
        }

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = parent.TenantId,
            ParentId = repositoryId,
            Name = request.Name,
            // Assigned the Folder mask now; if a version is later added, finalize reclassifies it (ADR "Folder mask on folders").
            MaskVersionId = await Documents.FolderMask.CurrentVersionIdAsync(_dbContext, cancellationToken),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(document);

        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw DocumentNameConflictException.OnSameParent();
        }

        await _queue.EnqueueAsync(document.Id, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentCreated, "Document", document.Id, document.Name, cancellationToken: cancellationToken);

        var resource = new DocumentSummaryResource
        {
            Id = document.Id,
            Name = document.Name,
            Links = [new Link("self", $"/api/documents/{document.Id}", "GET")],
        };

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId = document.Id }, resource);
    }

    public class RecycleBinItemResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public DateTimeOffset DeletedAt { get; set; }
    }

    public class RecycleBinResource : HypermediaResource
    {
        public List<RecycleBinItemResource> Items { get; set; } = [];
    }

    // Lists every deleted Document at any depth under this repository (ADR "Document delete/restore
    // (recycle bin) implementation") — collected via iterative level-by-level traversal (same pattern as
    // DocumentsController's cascade delete/restore), not a denormalized RepositoryId column (removed, see
    // ADR "Repository/Document unification"). IgnoreQueryFilters(["SoftDeleteFilter"]) only, the tenant
    // filter still applies. Each item carries its own "restore" hypermedia link (ADR 0003), so a client
    // doesn't need to know the restore route in advance.
    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet("{repositoryId:guid}/recycle-bin")]
    public async Task<IActionResult> ListRecycleBin(Guid repositoryId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await RepositoryExistsAsync(repositoryId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(repositoryId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);
        var descendantIds = await CollectDescendantIdsAsync(repositoryId, cancellationToken);

        var query = _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => descendantIds.Contains(d.Id) && d.DeletedAt != null);

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(d => d.CreatedAt > cursorCreatedAt || (d.CreatedAt == cursorCreatedAt && d.Id > cursorId));
        }

        var fetched = await query.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(ListRecycleBin), new { repositoryId, cursor, limit = pageSize })!, "GET"),
            new("purge-all", $"/api/repositories/{repositoryId}/recycle-bin/purge", "POST"),
        };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(ListRecycleBin), new { repositoryId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new RecycleBinResource
        {
            Items = page.Select(d => new RecycleBinItemResource
            {
                Id = d.Id,
                Name = d.Name,
                DeletedAt = d.DeletedAt!.Value,
                Links = new List<Link>
                {
                    new("restore", $"/api/documents/{d.Id}/restore", "POST"),
                    new("purge", $"/api/documents/{d.Id}/purge", "POST"),
                },
            }).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{repositoryId:guid}/recycle-bin")]
    public async Task<IActionResult> HeadRecycleBin(Guid repositoryId, CancellationToken cancellationToken)
    {
        if (!await RepositoryExistsAsync(repositoryId, cancellationToken))
        {
            return NotFound();
        }

        if (!await CanSeeAsync(repositoryId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    // Empties the repository's recycle bin — permanently purges every soft-deleted document under it (blobs +
    // rows + search index), irreversibly. Tenant-admin-only; a destructive action sub-resource (POST). Any item
    // somehow under a legal hold is left behind. See ADR "Manual hard-delete / purge".
    [HttpPost("{repositoryId:guid}/recycle-bin/purge")]
    public async Task<IActionResult> EmptyRecycleBin(Guid repositoryId, CancellationToken cancellationToken)
    {
        if (!await RepositoryExistsAsync(repositoryId, cancellationToken))
        {
            return NotFound();
        }

        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var descendantIds = await CollectDescendantIdsAsync(repositoryId, cancellationToken);
        var deleted = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => descendantIds.Contains(d.Id) && d.DeletedAt != null)
            .ToListAsync(cancellationToken);

        // Defensive: never purge a held item (a recycle-bin item can't be held by construction).
        var heldIds = deleted.Count == 0
            ? new List<Guid>()
            : await _dbContext.LegalHoldItems
                .Where(i => deleted.Select(d => d.Id).Contains(i.DocumentId) && _dbContext.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null))
                .Select(i => i.DocumentId)
                .ToListAsync(cancellationToken);
        var toPurge = deleted.Where(d => !heldIds.Contains(d.Id)).ToList();

        var purged = await _purger.PurgeAsync(toPurge, cancellationToken);
        foreach (var (id, name) in purged)
        {
            await _audit.RecordAsync(AuditActions.DocumentPurged, "Document", id, name, cancellationToken: cancellationToken);
        }

        return NoContent();
    }

    // Imports an export archive (ADR "Repository import") as a brand-new top-level repository (the archive root
    // becomes a root document). Requires CanImport (ADR "Dedicated CanExport/CanImport rights"). The root is
    // auto-renamed if its name collides with an existing repository.
    // A real export/migration archive can be gigabytes (a whole legacy-DMS subtree of scanned TIFFs/PDFs), so lift the
    // default 30 MB Kestrel + multipart limits on this upload — CanImport already gates it. ASP.NET buffers the
    // large IFormFile to a temp file, so this doesn't hold the archive in memory.
    [HttpPost("import")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Import(IFormFile file, [FromQuery] bool updateExisting, [FromQuery] bool includePermissions, CancellationToken cancellationToken)
    {
        if (!await HasImportRightAsync(cancellationToken))
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            throw new NoFileException();
        }

        _importer.SetImporter(_currentUserAccessor.UserId);
        await using var stream = file.OpenReadStream();
        var result = await _importer.ImportAsync(stream, null, updateExisting, includePermissions, merge: false, Documents.LeafMergeMode.Rename, cancellationToken);

        await _audit.RecordAsync(AuditActions.DocumentImported, "Document", result.RootDocumentId, result.RootName, $"{result.Documents} documents, {result.Versions} versions, {result.Skipped} already imported", cancellationToken: cancellationToken);

        return Ok(new
        {
            rootId = result.RootDocumentId,
            rootName = result.RootName,
            documents = result.Documents,
            versions = result.Versions,
            comments = result.Comments,
            skipped = result.Skipped,
            links = new[] { new Link("self", $"/api/repositories/{result.RootDocumentId}", "GET") },
        });
    }

    // Permanent destruction is tenant-admin-only (a User right; a ServiceAccount has no IsTenantAdmin).
    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;
        }

        return false;
    }

    // The caller's effective CanImport — a User's own-∪-groups rights (ADR "Enforce group system rights for
    // members") or a ServiceAccount's own column (ADR "Dedicated CanExport/CanImport rights").
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

    private async Task<List<Guid>> CollectDescendantIdsAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var descendantIds = new List<Guid>();
        var currentLevelIds = new List<Guid> { rootId };

        while (currentLevelIds.Count > 0)
        {
            var children = await _dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => d.ParentId != null && currentLevelIds.Contains(d.ParentId!.Value))
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            if (children.Count == 0)
            {
                break;
            }

            descendantIds.AddRange(children);
            currentLevelIds = children;
        }

        return descendantIds;
    }

    private async Task<bool> RepositoryExistsAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        return await _dbContext.Documents.AnyAsync(d => d.Id == repositoryId && d.ParentId == null, cancellationToken);
    }

    // Checks ServiceAccount first, then a logged-in User — the two accessors are mutually exclusive per
    // request (CurrentPrincipalMiddleware's three-way branch). See ADR "Document-scope authorization
    // retrofit for User, and tenant-administrator-driven onboarding".
    private Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken) =>
        _access.GetCallerRightsAsync(documentId, cancellationToken);

    private async Task<bool> CanSeeAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        return (await GetCallerRightsAsync(repositoryId, cancellationToken)).CanSee;
    }

    // Returns whichever principal actually made this request, for Document creator attribution
    // (CreatedByUserId/CreatedByServiceAccountId, CHECK-constrained to exactly one).
    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity() => _access.GetCallerIdentity();
}
