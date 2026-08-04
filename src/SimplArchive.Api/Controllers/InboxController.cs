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
using SimplArchive.Infrastructure.Acl;
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
        IAuditRecorder audit,
        IUserSystemRightsResolver userSystemRights)
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
    }

    private readonly ILegalHoldService _legalHold;
    private readonly IStorageQuotaService _storageQuota;
    private readonly IAuditRecorder _audit;
    private readonly IUserSystemRightsResolver _userSystemRights;

    public class InboxItemResource : HypermediaResource
    {
        public string Name { get; set; } = "";

        public long Size { get; set; }

        public DateTimeOffset LastModified { get; set; }

        // True when a `{name}.mask.json` sidecar exists — the item has a staged mask/index-data draft.
        public bool HasMask { get; set; }

        // The source of a group-inbox item (ADR 0532): the group's id + name, so the client labels it `[GroupName]`
        // and its action links already carry `?group=`. Null for the caller's own inbox items.
        public Guid? GroupId { get; set; }

        public string? GroupName { get; set; }

        // The source of another user's inbox item (ADR 0532), shown only to a CanManageInboxes holder viewing a
        // user's inbox: the user's id + name, so the client labels it and its links carry `?user=`. Null otherwise.
        public Guid? UserId { get; set; }

        public string? UserName { get; set; }
    }

    public class InboxResource : HypermediaResource
    {
        public List<InboxItemResource> Items { get; set; } = [];
    }

    // A group the caller belongs to — an upload-target choice for a group inbox (ADR 0532).
    public class InboxGroupResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }

    public class InboxGroupsResource : HypermediaResource
    {
        public List<InboxGroupResource> Groups { get; set; } = [];
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

    // Move an inbox item into another inbox (ADR 0532) — exactly one target: a group's inbox (any group in the
    // tenant) or a user's inbox (any user). Both null / both set is a 400.
    public class MoveInboxRequest
    {
        public Guid? TargetGroupId { get; set; }

        public Guid? TargetUserId { get; set; }
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

        // Staged OCR languages (ordered Tesseract codes) for a scannable item (.tif/.tiff/.pdf), consumed at
        // filing to set the version's OcrLanguages before the searchable-PDF conversion (ADR "Inbox OCR-language
        // staging"). Null/empty = the tenant default.
        public List<string>? OcrLanguages { get; set; }
    }

    public class InboxMaskFieldResource
    {
        public Guid FieldDefinitionId { get; set; }

        public List<string> Values { get; set; } = [];
    }

    private (Guid TenantId, Guid UserId)? Scope() =>
        _currentTenantAccessor.TenantId is { } tenantId && _currentUserAccessor.UserId is { } userId ? (tenantId, userId) : null;

    private static string Prefix(Guid tenantId, Guid userId) => $"tenants/{tenantId}/users/{userId}/inbox/";

    // A group inbox is the exact peer of the per-user inbox, keyed by group (ADR 0532) — implicit for every group,
    // access = effective group membership.
    private static string GroupPrefix(Guid tenantId, Guid groupId) => $"tenants/{tenantId}/groups/{groupId}/inbox/";

    private sealed record InboxScope(Guid TenantId, Guid UserId, string Prefix);

    // Resolves + authorizes the storage scope of an inbox item addressed by name + an optional source selector
    // (ADR 0532): own inbox (neither set), a group inbox the caller is an effective member of (`group`), or a
    // specific user's inbox (`user`) — the caller's own, or any user's if the caller holds CanManageInboxes.
    // null → the caller may not act on it (Forbid); item existence stays a separate NotFound check. A mask sidecar
    // is never addressable as an item. Scope.UserId is always the CALLER (filing attribution / rights checks);
    // Scope.Prefix is where the object actually lives.
    private async Task<InboxScope?> ResolveScopeAsync(Guid? group, Guid? user, string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, callerId) || IsMaskSidecar(name))
        {
            return null;
        }

        if (group is { } groupId)
        {
            var groups = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(_dbContext, callerId, cancellationToken);
            return groups.Contains(groupId) ? new InboxScope(tenantId, callerId, GroupPrefix(tenantId, groupId)) : null;
        }

        if (user is { } userId && userId != callerId)
        {
            // Another user's inbox — admin-gated (CanManageInboxes).
            return await CanManageInboxesAsync(callerId, cancellationToken)
                ? new InboxScope(tenantId, callerId, Prefix(tenantId, userId))
                : null;
        }

        // Neither source (or ?user= is the caller themselves) → the caller's own inbox.
        return new InboxScope(tenantId, callerId, Prefix(tenantId, callerId));
    }

    private async Task<bool> CanManageInboxesAsync(Guid userId, CancellationToken cancellationToken) =>
        (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageInboxes;

    // The `?group=`/`?user=` source query for an item's action links, so the client keeps acting on the right
    // prefix; own-inbox items carry no query.
    private static string SourceQuery(Guid? group, Guid? user) =>
        group is { } g ? $"?group={g}" : user is { } u ? $"?user={u}" : "";

    private static string ItemHref(string name, string suffix, Guid? group, Guid? user)
    {
        var path = suffix.Length == 0 ? $"/api/inbox/{Uri.EscapeDataString(name)}" : $"/api/inbox/{Uri.EscapeDataString(name)}/{suffix}";
        return path + SourceQuery(group, user);
    }

    private static bool IsMaskSidecar(string name) => name.EndsWith(MaskSidecarSuffix, StringComparison.OrdinalIgnoreCase);

    private static string SidecarName(string name) => name + MaskSidecarSuffix;

    // Preview renditions + the text-layout sidecar are cached next to the item (`<stem>.preview.*`,
    // `<stem>.textlayout.json` — ADR "Server-side preview renditions"/"Search hit overlay"). They must never
    // appear as inbox items, and are swept when the item leaves the inbox (ADR "Avoid inbox preview litter").
    private static bool IsDerivedArtifact(string name) =>
        name.Contains(".preview.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".textlayout.json", StringComparison.OrdinalIgnoreCase);

    // The caller's inbox. By default shows only the caller's OWN items (ADR 0532's "show own only" filter, on by
    // default). `?includeGroups=true` also aggregates the inbox of every group the caller is an effective member of
    // (each item labelled `[GroupName]`, carrying `?group=` on its links). `?user={id}` opens a specific user's
    // inbox instead — the caller's own, or any user's for a CanManageInboxes holder (else 403).
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeGroups, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, callerId))
        {
            return Forbid();
        }

        // A CanManageInboxes holder viewing another user's inbox (the user-picker path).
        if (user is { } targetUserId && targetUserId != callerId)
        {
            if (!await CanManageInboxesAsync(callerId, cancellationToken))
            {
                return Forbid();
            }

            var targetName = await _dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken);
            if (targetName is null)
            {
                return NotFound();
            }

            var userItems = await ListPrefixItemsAsync(Prefix(tenantId, targetUserId), group: null, groupName: null, user: targetUserId, userName: targetName, cancellationToken);
            return Ok(new InboxResource { Items = userItems, Links = [new Link("self", "/api/inbox", "GET")] });
        }

        // The caller's own inbox, plus — opt-in via the filter — their group inboxes (alphabetical, stable order).
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

        return Ok(new InboxResource { Items = items, Links = [new Link("self", "/api/inbox", "GET")] });
    }

    // Lists the (non-sidecar, non-derived) items under one inbox prefix — the caller's own (both null), a group's
    // (group + name), or another user's (user + name, admin-only). Each item carries its source so the client
    // labels + addresses it correctly, and a `move` link for the Send-to / Move-to-my-inbox actions (ADR 0532).
    private async Task<List<InboxItemResource>> ListPrefixItemsAsync(string prefix, Guid? group, string? groupName, Guid? user, string? userName, CancellationToken cancellationToken)
    {
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
                GroupId = group,
                GroupName = groupName,
                UserId = user,
                UserName = userName,
                Size = storageObject.Size,
                LastModified = storageObject.LastModified,
                HasMask = names.Contains(SidecarName(name)),
                Links =
                [
                    new Link("download", download.ToString(), "GET"),
                    new Link("preview", ItemHref(name, "preview", group, user), "GET"),
                    new Link("mask", ItemHref(name, "mask", group, user), "GET"),
                    new Link("file", ItemHref(name, "file", group, user), "POST"),
                    new Link("move", ItemHref(name, "move", group, user), "POST"),
                    new Link("self", ItemHref(name, "", group, user), "DELETE"),
                ],
            });
        }

        return items;
    }

    // The groups the caller is an effective member of (ADR 0532) — the upload-target choices for a group inbox
    // (a group's inbox exists implicitly, so a member can drop into it even while it's empty and wouldn't yet
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
                .Select(g => new InboxGroupResource { Id = g.Id, Name = g.Name })
                .ToListAsync(cancellationToken);

        return Ok(new InboxGroupsResource { Groups = groups, Links = [new Link("self", "/api/inbox/groups", "GET")] });
    }

    [HttpHead("groups")]
    public IActionResult GroupsHead() => Scope() is null ? Forbid() : NoContent();

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => Scope() is null ? Forbid() : NoContent();

    // Returns a presigned PUT URL so the client uploads a file straight into the inbox prefix (the Api never
    // proxies bytes). MinIO CORS (the same wildcard the drag-drop upload uses) allows the browser PUT.
    [HttpPost]
    public async Task<IActionResult> Upload([FromBody] UploadInboxRequest request, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(request.FileName?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InboxFilenameRequiredException();
        }

        // Own inbox when `group` is absent, else a group inbox the caller is an effective member of (ADR 0532) —
        // a non-member (or a `.mask.json` name) resolves to Forbid.
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var key = scope.Prefix + name;
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
    public async Task<IActionResult> Preview(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
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
                new Link("self", ItemHref(name, "preview", group, user), "GET"),
                new Link("preview-pages", ItemHref(name, "preview-pages", group, user), "GET"),
                new Link("text-layout", ItemHref(name, "text-layout", group, user), "GET"),
            ],
        });
    }

    [HttpHead("{name}/preview")]
    public async Task<IActionResult> PreviewHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        return await _objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken) ? NoContent() : NotFound();
    }

    // Ordered per-page image URLs for a multi-page TIFF; 204 for every other format (the client uses `preview`).
    [HttpGet("{name}/preview-pages")]
    public async Task<IActionResult> PreviewPages(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
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

        var pages = await _documentPreviewService.GetPreviewPagesAsync(key, PresignedUrlExpiry, cancellationToken: cancellationToken);
        if (pages is null)
        {
            return NoContent();
        }

        return Ok(new InboxPreviewPagesResource
        {
            Converted = pages.IsConverted,
            Pages = pages.Urls.Select(u => new InboxPreviewPageResource { Url = u.ToString() }).ToList(),
            Links = [new Link("self", ItemHref(name, "preview-pages", group, user), "GET")],
        });
    }

    [HttpHead("{name}/preview-pages")]
    public async Task<IActionResult> PreviewPagesHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken) =>
        await PreviewHead(name, group, user, cancellationToken);

    // Per-page word boxes for hit-overlay / find-in-document, via the text-layout service on the object key.
    [HttpGet("{name}/text-layout")]
    public async Task<IActionResult> TextLayout(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
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
            Links = [new Link("self", ItemHref(name, "text-layout", group, user), "GET")],
        });
    }

    [HttpHead("{name}/text-layout")]
    public async Task<IActionResult> TextLayoutHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken) =>
        await PreviewHead(name, group, user, cancellationToken);

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

        var draft = await ReadMaskSidecarAsync(scope.Prefix, name, cancellationToken) ?? new InboxMaskResource();
        draft.Links = [new Link("self", ItemHref(name, "mask", group, user), "GET")];
        return Ok(draft);
    }

    [HttpHead("{name}/mask")]
    public async Task<IActionResult> GetMaskHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken) =>
        await PreviewHead(name, group, user, cancellationToken);

    // Writes (or, for "(No mask)", clears) the staged mask/index-data draft sidecar. A staging draft, not a
    // filed document, so no required-field/format validation runs here — that happens if/when the item is filed.
    [HttpPut("{name}/mask")]
    public async Task<IActionResult> SetMask(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, [FromBody] InboxMaskResource request, CancellationToken cancellationToken)
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

        var payload = JsonSerializer.SerializeToUtf8Bytes(new InboxMaskResource
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

    private async Task<InboxMaskResource?> ReadMaskSidecarAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var sidecarKey = prefix + SidecarName(name);
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
    public async Task<IActionResult> File(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, [FromBody] FileInboxRequest request, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var (tenantId, userId, prefix) = scope;
        var inboxKey = prefix + name;
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
            return await FileAsVersionAsync(tenantId, userId, name, inboxKey, targetDocumentId, request.Comment, prefix, cancellationToken);
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
        if (!isEmail && await ReadMaskSidecarAsync(prefix, name, cancellationToken) is { } draft)
        {
            staged = new StagedClassification(
                draft.Name, draft.DocumentDate, draft.MaskId,
                draft.Fields.Select(f => (f.FieldDefinitionId, (IReadOnlyList<string>)f.Values)).ToList(),
                draft.OcrLanguages is { Count: > 0 } langs ? string.Join("+", langs) : null);
        }

        // Move the object out of the inbox to a normal document key (server-side copy within the bucket). The key
        // groups by the new document (ADR 0530): its filing year + a fresh storage folder, with the version id leaf.
        var storageFolderId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, storageFolderId, versionId, extension);
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

        // Confirm + classify (the object is already in storage). A staged draft applies the user's inbox
        // classification; otherwise the normal auto-classification runs — same path as a normal upload.
        await _finalizer.FinalizeAsync(version, cancellationToken, staged);

        // The item left the inbox — sweep its staged-mask sidecar + cached preview artifacts so they don't orphan.
        await PurgeItemArtifactsAsync(prefix, name, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentFiled, "Document", documentId, document.Name, group is null ? "Filed from inbox as a new document" : "Filed from a group inbox as a new document", cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId }, new { id = documentId, name = document.Name });
    }

    // Files the inbox item as the next Confirmed version of an existing document (ADR "Context-aware inbox
    // filing dialog"): moves the object to a document key and finalizes a new version. The document keeps its
    // existing classification (no re-classify, and a staged sidecar is ignored — it's an existing document).
    private async Task<IActionResult> FileAsVersionAsync(Guid tenantId, Guid userId, string name, string inboxKey, Guid documentId, string? comment, string prefix, CancellationToken cancellationToken)
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
        await _objectStorageClient.CopyObjectAsync(inboxKey, objectKey, cancellationToken);
        await _objectStorageClient.DeleteObjectAsync(inboxKey, cancellationToken);

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
        await _audit.RecordAsync(AuditActions.DocumentFiled, "Document", documentId, document.Name, "Filed from inbox as a new version", cancellationToken: cancellationToken);

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

    // Moves an inbox item from its source (own / a group I'm a member of / a user's — admin) into a target inbox:
    // any group or any user in the tenant (ADR 0532). A move — the object + its staged-mask sidecar relocate; the
    // source's cached preview artifacts are swept. Idempotent under contention (a vanished source → 404).
    [HttpPost("{name}/move")]
    public async Task<IActionResult> Move(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, [FromBody] MoveInboxRequest request, CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        // Exactly one target (both-null or both-set is invalid).
        if (request.TargetGroupId is null == request.TargetUserId is null)
        {
            throw new InboxMoveTargetRequiredException();
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

        await _objectStorageClient.DeleteObjectAsync(sourceKey, cancellationToken);
        await PurgeItemArtifactsAsync(sourcePrefix, name, cancellationToken);
        return NoContent();
    }

    // Sweeps an item's derived objects when it leaves the inbox: its `{name}.mask.json` staging sidecar plus
    // every cached preview/text-layout artifact sharing its stem (`<stem>.preview.*`, `<stem>.textlayout.json`).
    private async Task PurgeItemArtifactsAsync(string prefix, string name, CancellationToken cancellationToken)
    {
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
