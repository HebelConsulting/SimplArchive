using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Infrastructure.Intray;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Intray;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Errors.Exceptions.Storage;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Intray;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

using IntrayScope = SimplArchive.Api.Intray.IntrayScopeResolver.IntrayScope;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The per-user S3-backed intray (ADR "S3-backed inbox"): raw files staged under
/// `tenants/{tenantId}/users/{userId}/inbox/` (a sub-folder of the per-user private space, ADR "Per-user
/// object-storage prefix") — no DB entity. The clients list/upload/delete items; filing an
/// item creates a real Document + Confirmed version by moving the object to a normal document key (a
/// server-side S3 copy, no re-upload) and running the same auto-classifying finalize path. Scoped to the
/// caller's userId from the token — a ServiceAccount has no intray.
///
/// An item can carry a staged mask/index-data draft, stored as a sidecar object `{name}.mask.json` alongside
/// it (ADR "Inbox item classification + preview"). Sidecars are hidden from the listing; an item's `hasMask`
/// flag tells the client whether one exists (the desktop shows un-masked items in square brackets). Preview,
/// preview-pages and text-layout mirror the version endpoints against the intray object key — the
/// rendition/text-layout services are keyed purely by object key, so no Document is needed.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/intray")]
[Authorize]
public class IntrayController : ControllerBase
{
    // Internal: IntrayPreviewController (the derived-artifact sibling on these routes, issue #466) shares both.
    internal static readonly TimeSpan PresignedUrlExpiry = TimeSpan.FromMinutes(15);
    private const string MaskSidecarSuffix = IntrayPageService.MaskSidecarSuffix;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IDocumentPreviewService _documentPreviewService;
    private readonly IDocumentTextLayoutService _textLayoutService;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly DocumentFinalizer _finalizer;
    private readonly IntrayScopeResolver _scopes;

    public IntrayController(
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
        IAuditRecorder audit,
        IUserSystemRightsResolver userSystemRights,
        IntrayScopeResolver scopes)
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
        _userSystemRights = userSystemRights;
        _scopes = scopes;
    }

    private readonly ILegalHoldService _legalHold;
    private readonly IStorageQuotaService _storageQuota;
    private readonly IAuditRecorder _audit;
    private readonly IUserSystemRightsResolver _userSystemRights;

    public class IntrayItemResource : HypermediaResource
    {
        public string Name { get; set; } = "";

        public long Size { get; set; }

        public DateTimeOffset LastModified { get; set; }

        // True when a `{name}.mask.json` sidecar exists — the item has a staged mask/index-data draft.
        public bool HasMask { get; set; }

        // True when a `{name}.signed` sidecar exists: the content carries a digital signature (#491). Answered
        // from the prefix listing rather than the bytes, the same way HasMask is — reading each item to paint a
        // list would cost one download per row. The clients badge these, and offer no page operation on them,
        // because any rewrite voids a signature.
        public bool Signed { get; set; }

        // The source of a group-intray item (ADR 0532): the group's id + name, so the client labels it `[GroupName]`
        // and its action links already carry `?group=`. Null for the caller's own intray items.
        public Guid? GroupId { get; set; }

        public string? GroupName { get; set; }

        // The source of another user's intray item (ADR 0532), shown only to a CanManageIntrays holder viewing a
        // user's intray: the user's id + name, so the client labels it and its links carry `?user=`. Null otherwise.
        public Guid? UserId { get; set; }

        public string? UserName { get; set; }
    }

    public class IntrayResource : HypermediaResource
    {
        public List<IntrayItemResource> Items { get; set; } = [];
    }

    // A group the caller belongs to — an upload-target choice for a group intray (ADR 0532).
    public class IntrayGroupResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }

    public class IntrayGroupsResource : HypermediaResource
    {
        public List<IntrayGroupResource> Groups { get; set; } = [];
    }

    // A user in the tenant — a "Send to a user" / admin user-picker choice (ADR 0532).
    public class IntrayUserResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }

    public class IntrayUsersResource : HypermediaResource
    {
        public List<IntrayUserResource> Users { get; set; } = [];
    }

    public class UploadIntrayRequest
    {
        public string FileName { get; set; } = "";
    }

    public class UploadIntrayResource : HypermediaResource
    {
        public string Name { get; set; } = "";

        public Uri UploadUrl { get; set; } = null!;
    }

    public class FileIntrayRequest
    {
        public Guid FolderId { get; set; }

        // When set, the item is filed as a new *version* of this existing document instead of as a new document
        // in FolderId (ADR "Context-aware inbox filing dialog").
        public Guid? DocumentId { get; set; }

        // Optional override for the filed document's name; defaults to the intray filename.
        public string? Name { get; set; }

        // Optional feed comment posted on the resulting document (ADR "Filing posts a feed comment"); when
        // blank, a default "@{DisplayName} filed a new document." is posted.
        public string? Comment { get; set; }
    }

    // Move an intray item into another intray (ADR 0532) — exactly one target: a group's intray (any group in the
    // tenant) or a user's intray (any user). Both null / both set is a 400.
    public class MoveIntrayRequest
    {
        public Guid? TargetGroupId { get; set; }

        public Guid? TargetUserId { get; set; }
    }

    // The staged mask draft (also the on-the-wire shape and the sidecar JSON shape). MaskId null = "(No mask)".
    // Name/DocumentDate are staged system fields (the filed Document.Name / DocumentVersion.DocumentDate) —
    // DocumentDate is a "yyyy-MM-dd" string (ADR "Staged Name + Document date on inbox items").
    public class IntrayMaskResource : HypermediaResource
    {
        public string? Name { get; set; }

        public string? DocumentDate { get; set; }

        public Guid? MaskId { get; set; }

        public List<IntrayMaskFieldResource> Fields { get; set; } = [];

        // Staged OCR languages (ordered Tesseract codes) for a scannable item (.tif/.tiff/.pdf), consumed at
        // filing to set the version's OcrLanguages before the searchable-PDF conversion (ADR "Inbox OCR-language
        // staging"). Null/empty = the tenant default.
        public List<string>? OcrLanguages { get; set; }
    }

    public class IntrayMaskFieldResource
    {
        public Guid FieldDefinitionId { get; set; }

        public List<string> Values { get; set; } = [];
    }

    // Scope resolution + authorization is IntrayScopeResolver (ADR 0575) — shared with IntrayPagesController,
    // because an authorization rule with two implementations is one that gets tightened in only one of them.
    // These forwarders keep the call sites below reading as they did.
    private (Guid TenantId, Guid UserId)? Scope() => _scopes.Caller();

    private static string Prefix(Guid tenantId, Guid userId) => IntrayScopeResolver.UserPrefix(tenantId, userId);

    private static string GroupPrefix(Guid tenantId, Guid groupId) => IntrayScopeResolver.GroupPrefix(tenantId, groupId);

    private async Task<IntrayScope?> ResolveScopeAsync(Guid? group, Guid? user, string name, CancellationToken cancellationToken) =>
        await _scopes.ResolveAsync(group, user, name, cancellationToken);

    private async Task<bool> CanManageIntraysAsync(Guid userId, CancellationToken cancellationToken) =>
        await _scopes.CanManageIntraysAsync(userId, cancellationToken);

    // The `?group=`/`?user=` source query for an item's action links, so the client keeps acting on the right
    // prefix; own-intray items carry no query.
    private static string SourceQuery(Guid? group, Guid? user) =>
        group is { } g ? $"?group={g}" : user is { } u ? $"?user={u}" : "";

    internal static string ItemHref(string name, string suffix, Guid? group, Guid? user)
    {
        var path = suffix.Length == 0 ? $"/api/intray/{Uri.EscapeDataString(name)}" : $"/api/intray/{Uri.EscapeDataString(name)}/{suffix}";
        return path + SourceQuery(group, user);
    }

    private static bool IsMaskSidecar(string name) => IntrayScopeResolver.IsMaskSidecar(name);

    private static string SidecarName(string name) => name + MaskSidecarSuffix;

    // The collection's own actions, advertised where the collection is read (ADR 0557). `from-document` is how a
    // repository document is copied in as a template — an action no resource links to is unreachable by a
    // conforming client, and therefore incomplete (ADR 0543).
    private static List<Link> IntrayCollectionLinks() =>
    [
        new Link("self", "/api/intray", "GET"),
        new Link("from-document", "/api/intray/from-document", "POST"),
        // Joining several staged items into one (ADR 0575). A collection-level action, advertised where the
        // collection is read (ADR 0557) — the client enables it once the selection is two compatible items.
        new Link("join", "/api/intray/from-items", "POST"),
        // The printable Patch 3 separator sheet, and a sample batch made with it (ADR 0577).
        new Link("patchCodeSheet", "/api/intray/patch-code-sheet", "GET"),
        new Link("patchCodeSample", "/api/intray/patch-code-sample", "GET"),
        new Link("patchCodeSampleScan", "/api/intray/patch-code-sample-scan", "GET"),
    ];

    // Preview renditions + the text-layout sidecar are cached next to the item (`<stem>.preview.*`,
    // `<stem>.textlayout.json` — ADR "Server-side preview renditions"/"Search hit overlay"). They must never
    // appear as intray items, and are swept when the item leaves the intray (ADR "Avoid inbox preview litter").
    private static bool IsDerivedArtifact(string name) =>
        name.Contains(".preview.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".textlayout.json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(SimplArchive.Infrastructure.Intray.IntrayIngestPipeline.MarkerSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(SimplArchive.Infrastructure.Intray.IntrayIngestPipeline.SignedSuffix, StringComparison.OrdinalIgnoreCase);

    // The caller's intray. By default shows only the caller's OWN items (ADR 0532's "show own only" filter, on by
    // default). `?includeGroups=true` also aggregates the intray of every group the caller is an effective member of
    // (each item labelled `[GroupName]`, carrying `?group=` on its links). `?user={id}` opens a specific user's
    // intray instead — the caller's own, or any user's for a CanManageIntrays holder (else 403).
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeGroups, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, callerId))
        {
            return Forbid();
        }

        // A CanManageIntrays holder viewing another user's intray (the user-picker path).
        if (user is { } targetUserId && targetUserId != callerId)
        {
            if (!await CanManageIntraysAsync(callerId, cancellationToken))
            {
                return Forbid();
            }

            var targetName = await _dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken);
            if (targetName is null)
            {
                return NotFound();
            }

            var userItems = await ListPrefixItemsAsync(Prefix(tenantId, targetUserId), group: null, groupName: null, user: targetUserId, userName: targetName, cancellationToken);
            return Ok(new IntrayResource { Items = userItems, Links = IntrayCollectionLinks() });
        }

        // The caller's own intray, plus — opt-in via the filter — their group intrays (alphabetical, stable order).
        var items = await ListPrefixItemsAsync(Prefix(tenantId, callerId), group: null, groupName: null, user: null, userName: null, cancellationToken);

        if (includeGroups)
        {
            var groupIds = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, callerId, cancellationToken);
            if (groupIds.Count > 0)
            {
                var groups = await _dbContext.Groups
                    .Where(g => groupIds.Contains(g.Id))
                    .Select(g => new { g.Id, g.Name })
                    .ToListAsync(cancellationToken);
                foreach (var g in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                {
                    items.AddRange(await ListPrefixItemsAsync(GroupPrefix(tenantId, g.Id), g.Id, g.Name, user: null, userName: null, cancellationToken));
                }
            }
        }

        return Ok(new IntrayResource { Items = items, Links = IntrayCollectionLinks() });
    }

    // Lists the (non-sidecar, non-derived) items under one intray prefix — the caller's own (both null), a group's
    // (group + name), or another user's (user + name, admin-only). Each item carries its source so the client
    // labels + addresses it correctly, and a `move` link for the Send-to / Move-to-my-intray actions (ADR 0532).
    private async Task<List<IntrayItemResource>> ListPrefixItemsAsync(string prefix, Guid? group, string? groupName, Guid? user, string? userName, CancellationToken cancellationToken)
    {
        var objects = await _objectStorageClient.ListObjectsAsync(prefix, cancellationToken);

        // Names present in the prefix (used to answer "does this item have a mask sidecar?").
        var names = objects
            .Select(o => o.Key[prefix.Length..])
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.Ordinal);

        var items = new List<IntrayItemResource>();
        foreach (var storageObject in objects.OrderByDescending(o => o.LastModified))
        {
            var name = storageObject.Key[prefix.Length..];
            if (string.IsNullOrEmpty(name) || IsMaskSidecar(name) || IsDerivedArtifact(name))
            {
                continue; // the prefix placeholder, a hidden mask sidecar, or a cached preview/text-layout artifact
            }

            var download = await _objectStorageClient.GetPresignedDownloadUrlAsync(storageObject.Key, PresignedUrlExpiry, name, cancellationToken);

            items.Add(new IntrayItemResource
            {
                Name = name,
                GroupId = group,
                GroupName = groupName,
                UserId = user,
                UserName = userName,
                Size = storageObject.Size,
                LastModified = storageObject.LastModified,
                HasMask = names.Contains(SidecarName(name)),
                Signed = names.Contains(name + SimplArchive.Infrastructure.Intray.IntrayIngestPipeline.SignedSuffix),
                Links =
                [
                    new Link("download", download.ToString(), "GET"),
                    new Link("preview", ItemHref(name, "preview", group, user), "GET"),
                    new Link("mask", ItemHref(name, "mask", group, user), "GET"),
                    new Link("file", ItemHref(name, "file", group, user), "POST"),
                    new Link("move", ItemHref(name, "move", group, user), "POST"),
                    new Link("self", ItemHref(name, "", group, user), "DELETE"),
                    // Page operations (ADR 0575) — advertised from the NAME, which is all a listing can afford
                    // to know: reading every item's bytes to count pages would make opening the intray cost one
                    // download per row. Whether split/sort can actually succeed is then the pages resource's
                    // own answer.
                    .. PageComposer.FormatOf(name) != PageComposer.PageFormat.None
                        ? new[] { new Link("pages", ItemHref(name, "pages", group, user), "GET") }
                        : [],
                ],
            });
        }

        return items;
    }

    // The groups the caller is an effective member of (ADR 0532) — the upload-target choices for a group intray
    // (a group's intray exists implicitly, so a member can drop into it even while it's empty and wouldn't yet
    // appear as a source in the aggregated listing).
    [HttpGet("groups")]
    public async Task<IActionResult> Groups(CancellationToken cancellationToken)
    {
        if (Scope() is not var (_, userId))
        {
            return Forbid();
        }

        var groupIds = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, userId, cancellationToken);
        var groups = groupIds.Count == 0
            ? []
            : await _dbContext.Groups
                .Where(g => groupIds.Contains(g.Id))
                .OrderBy(g => g.Name)
                .Select(g => new IntrayGroupResource { Id = g.Id, Name = g.Name })
                .ToListAsync(cancellationToken);

        return Ok(new IntrayGroupsResource { Groups = groups, Links = [new Link("self", "/api/intray/groups", "GET")] });
    }

    [HttpHead("groups")]
    public IActionResult GroupsHead() => Scope() is null ? Forbid() : NoContent();

    // The tenant's other users (id + name) — the "Send to a user" picker choices, and the CanManageIntrays admin's
    // user-picker for opening a user's intray (ADR 0532). Any authenticated caller (a hand-off to a colleague);
    // active users only, excluding the caller.
    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken cancellationToken)
    {
        if (Scope() is not var (_, userId))
        {
            return Forbid();
        }

        var users = await _dbContext.Users
            .Where(u => u.IsActive && u.Id != userId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new IntrayUserResource { Id = u.Id, Name = u.DisplayName })
            .ToListAsync(cancellationToken);

        return Ok(new IntrayUsersResource { Users = users, Links = [new Link("self", "/api/intray/users", "GET")] });
    }

    [HttpHead("users")]
    public IActionResult UsersHead() => Scope() is null ? Forbid() : NoContent();

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => Scope() is null ? Forbid() : NoContent();

    // Returns a presigned PUT URL so the client uploads a file straight into the intray prefix (the Api never
    // proxies bytes). MinIO CORS (the same wildcard the drag-drop upload uses) allows the browser PUT.
    [HttpPost]
    public async Task<IActionResult> Upload([FromBody] UploadIntrayRequest request, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(request.FileName?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new IntrayFilenameRequiredException();
        }

        // Own intray when `group` is absent, else a group intray the caller is an effective member of (ADR 0532) —
        // a non-member (or a `.mask.json` name) resolves to Forbid.
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var key = scope.Prefix + name;
        var uploadUrl = await _objectStorageClient.GetPresignedUploadUrlAsync(key, PresignedUrlExpiry, cancellationToken);

        return Ok(new UploadIntrayResource
        {
            Name = name,
            UploadUrl = uploadUrl,
            Links =
            [
                new Link("self", "/api/intray", "GET"),
                // What the client MUST call once its PUT completes: the ingest pipeline — deskew, patch-code
                // cutting — runs there and nowhere else on this path. Without this rel the endpoint existed and
                // worked but no conforming client could reach it (ADR 0543: an action no resource links to is
                // unreachable, and therefore incomplete), so every upload silently waited up to five minutes
                // for IntrayIngestSweepWorker's fallback poll — which is the WebDAV safety net, not the design.
                new Link("processed", ItemHref(name, "processed", group, user), "POST"),
            ],
        });
    }

    /// <summary>
    /// Copies an existing document into the caller's intray as a STAGED item, carrying its mask and index values
    /// across as the staged draft (#467).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workflow this serves: base new work on an existing document as a TEMPLATE, without committing to a
    /// new document or a new version until it is filed. The intray is where work that is not yet a document
    /// belongs, so a template lands there with its mask already staged and the user edits what differs.
    /// </para>
    /// <para>
    /// Server-side on purpose. The alternative — the browser downloading the version and re-uploading it — sends
    /// the bytes twice over the user's connection and can leave a file with no sidecar if it fails midway. Here
    /// the object copy and the sidecar write are one request, and the caller needs no more rights than reading
    /// the source document.
    /// </para>
    /// </remarks>
    [HttpPost("from-document")]
    public async Task<IActionResult> CopyFromDocument([FromBody] IntrayFromDocumentRequest request, CancellationToken cancellationToken)
    {
        if (Scope() is not var (_, userId))
        {
            return Forbid();
        }

        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == request.DocumentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        // Reading the source is the only right required: nothing is created in the repository, and what lands in
        // the intray is the caller's own staged copy.
        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, document.Id, cancellationToken)).CanReadContent)
        {
            return Forbid();
        }

        var version = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
        if (version is null)
        {
            throw new IntraySourceHasNoVersionException(document.Name);
        }

        // The intray is addressed by NAME, so the copy keeps the document's name plus the version's extension —
        // which is also what lets a later drop onto Check-out match it back by filename.
        var name = Path.GetFileName(document.Name + Path.GetExtension(version.ObjectKey));
        if (await ResolveScopeAsync(group: null, user: null, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var itemKey = scope.Prefix + name;
        if (await _objectStorageClient.ExistsAsync(itemKey, cancellationToken))
        {
            throw new IntrayItemNameConflictException(name);
        }

        await _objectStorageClient.CopyObjectAsync(version.ObjectKey, itemKey, cancellationToken);

        // The staged draft: the source's mask and every index value it holds, so the template arrives filled in.
        var values = await _dbContext.FieldValues
            .Where(v => v.DocumentId == document.Id)
            .GroupBy(v => v.FieldDefinitionId)
            .Select(g => new { FieldDefinitionId = g.Key, Values = g.Select(v => v.Value).ToList() })
            .ToListAsync(cancellationToken);

        var draft = new IntrayMaskResource
        {
            Name = document.Name,
            MaskId = await _dbContext.MaskVersions.Where(mv => mv.Id == document.MaskVersionId)
                .Select(mv => (Guid?)mv.MaskId).FirstOrDefaultAsync(cancellationToken),
            DocumentDate = version.DocumentDate.ToString("yyyy-MM-dd"),
            Fields = values.Select(v => new IntrayMaskFieldResource { FieldDefinitionId = v.FieldDefinitionId, Values = v.Values }).ToList(),
        };

        await using var payload = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(draft));
        await _objectStorageClient.PutObjectAsync(scope.Prefix + SidecarName(name), payload, "application/json", cancellationToken);

        return Ok(new UploadIntrayResource
        {
            Name = name,
            // Nothing for the client to upload — the copy already happened server-side, which is the point.
            UploadUrl = new Uri("about:blank"),
            Links = [new Link("self", "/api/intray", "GET")],
        });
    }

    public class IntrayFromDocumentRequest
    {
        public Guid DocumentId { get; set; }
    }

    // Reads the staged mask/index-data draft from the `{name}.mask.json` sidecar; an empty draft (no sidecar).
    [HttpGet("{name}/mask")]
    public async Task<IActionResult> GetMask(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var itemKey = scope.Prefix + name;
        if (!await _objectStorageClient.ExistsAsync(itemKey, cancellationToken))
        {
            return NotFound();
        }

        var draft = await ReadMaskSidecarAsync(scope.Prefix, name, cancellationToken) ?? new IntrayMaskResource();
        draft.Links = [new Link("self", ItemHref(name, "mask", group, user), "GET")];
        return Ok(draft);
    }

    [HttpHead("{name}/mask")]
    public async Task<IActionResult> GetMaskHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        return await _objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken) ? NoContent() : NotFound();
    }

    // Writes (or, for "(No mask)", clears) the staged mask/index-data draft sidecar. A staging draft, not a
    // filed document, so no required-field/format validation runs here — that happens if/when the item is filed.
    [HttpPut("{name}/mask")]
    public async Task<IActionResult> SetMask(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, [FromBody] IntrayMaskResource request, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var itemKey = scope.Prefix + name;
        if (!await _objectStorageClient.ExistsAsync(itemKey, cancellationToken))
        {
            return NotFound();
        }

        var sidecarKey = scope.Prefix + SidecarName(name);

        // No mask, no field values, no name and no date → nothing staged, so remove the sidecar and the item
        // reads as un-classified (square brackets).
        if (request.MaskId is null && request.Fields.All(f => f.Values.Count == 0)
            && string.IsNullOrWhiteSpace(request.Name) && string.IsNullOrWhiteSpace(request.DocumentDate)
            && (request.OcrLanguages is null || request.OcrLanguages.Count == 0))
        {
            if (await _objectStorageClient.ExistsAsync(sidecarKey, cancellationToken))
            {
                await _objectStorageClient.DeleteObjectAsync(sidecarKey, cancellationToken);
            }

            return NoContent();
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new IntrayMaskResource
        {
            Name = request.Name,
            DocumentDate = request.DocumentDate,
            MaskId = request.MaskId,
            Fields = request.Fields,
            OcrLanguages = request.OcrLanguages,
        });
        using var stream = new MemoryStream(payload);
        await _objectStorageClient.PutObjectAsync(sidecarKey, stream, "application/json", cancellationToken);
        return NoContent();
    }

    private async Task<IntrayMaskResource?> ReadMaskSidecarAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var sidecarKey = prefix + SidecarName(name);
        if (!await _objectStorageClient.ExistsAsync(sidecarKey, cancellationToken))
        {
            return null;
        }

        await using var stream = await _objectStorageClient.GetObjectAsync(sidecarKey, cancellationToken);
        return await JsonSerializer.DeserializeAsync<IntrayMaskResource>(stream, cancellationToken: cancellationToken);
    }

    // Files an intray item into a repository folder: moves its object to a normal document key (server-side
    // copy + delete) and creates a Document + Confirmed version via the shared auto-classifying finalize path.
    [HttpPost("{name}/file")]
    public async Task<IActionResult> File(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, [FromBody] FileIntrayRequest request, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var (tenantId, userId, prefix) = scope;
        var intrayKey = prefix + name;
        if (!await _objectStorageClient.ExistsAsync(intrayKey, cancellationToken))
        {
            return NotFound();
        }

        // Storage-quota enforcement (ADR "Per-tenant storage quota"): reject filing that would push the tenant past
        // its quota BEFORE the object is moved out of the intray, so the item is preserved on rejection. Covers both
        // file-into-folder and file-as-version (each adds a confirmed blob).
        var intraySizeBytes = await _objectStorageClient.GetObjectSizeAsync(intrayKey, cancellationToken);
        if (!await _storageQuota.CanStoreAsync(tenantId, intraySizeBytes, cancellationToken))
        {
            throw new StorageQuotaExceededException("Filing this item would exceed the tenant's storage quota.");
        }

        // File as a new version of an existing document instead of as a new document in a folder.
        if (request.DocumentId is { } targetDocumentId)
        {
            return await FileAsVersionAsync(tenantId, userId, name, intrayKey, targetDocumentId, request.Comment, prefix, cancellationToken);
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == request.FolderId, cancellationToken))
        {
            throw new FolderNotFoundException();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, request.FolderId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        // Split the name: the intray file's extension goes on the object key, the stem becomes Document.Name
        // (ADR "Extension off Document.Name, derived from the object key").
        var rawName = string.IsNullOrWhiteSpace(request.Name) ? name : request.Name.Trim();
        var extension = Path.GetExtension(name);
        var documentName = Path.GetFileNameWithoutExtension(rawName);
        var now = DateTimeOffset.UtcNow;

        // Consume the staged classification draft, if any (ADR "Consume the staged mask sidecar at filing").
        // Emails are never staged (they aren't offered a mask in the intray) — they always auto-classify.
        var isEmail = extension is ".eml" or ".msg";
        StagedClassification? staged = null;
        if (!isEmail && await ReadMaskSidecarAsync(prefix, name, cancellationToken) is { } draft)
        {
            staged = new StagedClassification(
                draft.Name, draft.DocumentDate, draft.MaskId,
                draft.Fields.Select(f => (f.FieldDefinitionId, (IReadOnlyList<string>)f.Values)).ToList(),
                draft.OcrLanguages is { Count: > 0 } langs ? string.Join("+", langs) : null);
        }

        // Move the object out of the intray to a normal document key (server-side copy within the bucket). The key
        // groups by the new document (ADR 0530): its filing year + a fresh storage folder, with the version id leaf.
        var storageFolderId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, storageFolderId, versionId, extension);
        await _objectStorageClient.CopyObjectAsync(intrayKey, objectKey, cancellationToken);
        await _objectStorageClient.DeleteObjectAsync(intrayKey, cancellationToken);

        var documentId = Guid.NewGuid();
        var document = new Document
        {
            Id = documentId,
            TenantId = tenantId,
            ParentId = request.FolderId,
            Name = documentName,
            CreatedByUserId = userId,
            CreatedAt = now,
            StorageFolderId = storageFolderId,
        };

        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
            // The filing comment is the version's "why this revision" note (ADR 0528), not a chat post.
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
        };

        _dbContext.Documents.Add(document);
        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Confirm + classify (the object is already in storage). A staged draft applies the user's intray
        // classification; otherwise the normal auto-classification runs — same path as a normal upload.
        // The finalizer assigns the mask, which is where the destination's admission rules become answerable —
        // so a folder that will not hold this document is refused HERE rather than at creation (#644). Without
        // the translation it surfaces as a bare 500: the Domain exception is an InvalidOperationException, and
        // nothing on this path knew to map it.
        try
        {
            await _finalizer.FinalizeAsync(version, cancellationToken, staged);
        }
        catch (Domain.Documents.PersonalSpaceStructureException e)
        {
            throw new Errors.Exceptions.Documents.PersonalSpaceStructureException(e.Message);
        }

        // The item left the intray — sweep its staged-mask sidecar + cached preview artifacts so they don't orphan.
        await PurgeItemArtifactsAsync(prefix, name, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentFiled, "Document", documentId, document.Name, group is null ? "Filed from intray as a new document" : "Filed from a group intray as a new document", cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId }, new { id = documentId, name = document.Name });
    }

    // Files the intray item as the next Confirmed version of an existing document (ADR "Context-aware inbox
    // filing dialog"): moves the object to a document key and finalizes a new version. The document keeps its
    // existing classification (no re-classify, and a staged sidecar is ignored — it's an existing document).
    private async Task<IActionResult> FileAsVersionAsync(Guid tenantId, Guid userId, string name, string intrayKey, Guid documentId, string? comment, string prefix, CancellationToken cancellationToken)
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
        // The key groups by the document's storage folder (ADR 0530), bucketed by the VERSION's filing year (ADR
        // 0520) — filed now — with the new version id as the leaf.
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, document.StorageFolderId, versionId, Path.GetExtension(name));
        await _objectStorageClient.CopyObjectAsync(intrayKey, objectKey, cancellationToken);
        await _objectStorageClient.DeleteObjectAsync(intrayKey, cancellationToken);

        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
            // The check-in comment is the new version's "why this revision" note (ADR 0528), not a chat post.
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
        };

        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _finalizer.FinalizeAsync(version, cancellationToken); // no staged draft — existing document keeps its mask
        await PurgeItemArtifactsAsync(prefix, name, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentFiled, "Document", documentId, document.Name, "Filed from intray as a new version", cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId }, new { id = documentId, name = document.Name });
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var key = scope.Prefix + name;
        if (!await _objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        await _objectStorageClient.DeleteObjectAsync(key, cancellationToken);
        await PurgeItemArtifactsAsync(scope.Prefix, name, cancellationToken);
        return NoContent();
    }

    // Moves an intray item from its source (own / a group I'm a member of / a user's — admin) into a target intray:
    // any group or any user in the tenant (ADR 0532). A move — the object + its staged-mask sidecar relocate; the
    // source's cached preview artifacts are swept. Idempotent under contention (a vanished source → 404).
    [HttpPost("{name}/move")]
    public async Task<IActionResult> Move(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, [FromBody] MoveIntrayRequest request, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        // Exactly one target (both-null or both-set is invalid).
        if (request.TargetGroupId is null == request.TargetUserId is null)
        {
            throw new IntrayMoveTargetRequiredException();
        }

        var (tenantId, _, sourcePrefix) = scope;
        var sourceKey = sourcePrefix + name;
        if (!await _objectStorageClient.ExistsAsync(sourceKey, cancellationToken))
        {
            return NotFound();
        }

        string targetPrefix;
        if (request.TargetGroupId is { } targetGroupId)
        {
            if (!await _dbContext.Groups.AnyAsync(g => g.Id == targetGroupId, cancellationToken))
            {
                return NotFound();
            }

            targetPrefix = GroupPrefix(tenantId, targetGroupId);
        }
        else
        {
            var targetUserId = request.TargetUserId!.Value;
            if (!await _dbContext.Users.AnyAsync(u => u.Id == targetUserId && u.IsActive, cancellationToken))
            {
                return NotFound();
            }

            targetPrefix = Prefix(tenantId, targetUserId);
        }

        if (targetPrefix == sourcePrefix)
        {
            return NoContent(); // already there — a no-op
        }

        // Relocate the object + its staged-mask sidecar, then sweep the source (its sidecar + cached preview
        // artifacts) so nothing orphans; the preview/text-layout regenerate on demand at the target.
        await _objectStorageClient.CopyObjectAsync(sourceKey, targetPrefix + name, cancellationToken);
        var sidecarKey = sourcePrefix + SidecarName(name);
        if (await _objectStorageClient.ExistsAsync(sidecarKey, cancellationToken))
        {
            await _objectStorageClient.CopyObjectAsync(sidecarKey, targetPrefix + SidecarName(name), cancellationToken);
        }

        // The ingest marker + signature flag describe the ITEM's history, not its location — they travel with
        // it, or the target intray's sweep would put an already-straightened file through the pipeline again.
        foreach (var suffix in new[] { IntrayIngestPipeline.MarkerSuffix, IntrayIngestPipeline.SignedSuffix })
        {
            var markerKey = sourcePrefix + name + suffix;
            if (await _objectStorageClient.ExistsAsync(markerKey, cancellationToken))
            {
                await _objectStorageClient.CopyObjectAsync(markerKey, targetPrefix + name + suffix, cancellationToken);
            }
        }

        await _objectStorageClient.DeleteObjectAsync(sourceKey, cancellationToken);
        await PurgeItemArtifactsAsync(sourcePrefix, name, cancellationToken);
        return NoContent();
    }

    // Sweeps an item's derived objects when it leaves the intray: its `{name}.mask.json` staging sidecar, every
    // cached preview/text-layout artifact sharing its stem (`<stem>.preview.*`, `<stem>.textlayout.json`), and
    // the ingest marker + signature flag. The marker matters most: leaving `{name}.ingest.json` behind made a
    // RE-UPLOAD under the same name skip the whole pipeline — no straighten, no cut — in a 4 ms no-op that
    // looked exactly like "ingest is broken" (review finding, 2026-08-16).
    private async Task PurgeItemArtifactsAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var lastDot = name.LastIndexOf('.');
        var stem = lastDot >= 0 ? name[..lastDot] : name;

        foreach (var storageObject in await _objectStorageClient.ListObjectsAsync(prefix, cancellationToken))
        {
            var candidate = storageObject.Key[prefix.Length..];
            var isArtifact = candidate == SidecarName(name)
                || candidate.StartsWith($"{stem}.preview.", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals($"{stem}.textlayout.json", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals($"{name}{IntrayIngestPipeline.MarkerSuffix}", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals($"{name}{IntrayIngestPipeline.SignedSuffix}", StringComparison.OrdinalIgnoreCase);
            if (isArtifact)
            {
                await _objectStorageClient.DeleteObjectAsync(storageObject.Key, cancellationToken);
            }
        }
    }
}
