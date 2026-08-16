using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

// Raised for an Api action that failed with a message worth showing the user (duplicate name, no permission).
// Base for a real error condition surfaced by SimplArchiveApiClient — carries a user-facing message the crash
// guard / status line displays. No longer sealed: intent-named conditions subclass it (per CLAUDE.md's
// exception-type rule) while the existing catch(ApiActionException) surfacing still picks them up.
public class ApiActionException(string message) : Exception(message);

// Set-primary-location / promote-a-reference errors (ADR 0506) — a small family under ApiActionException so a
// caller can catch the whole group and the status line still shows the message. Each type fixes its own message.
public class PrimaryLocationException(string message) : ApiActionException(message);

public sealed class CannotSetPrimaryLocationException()
    : PrimaryLocationException("Can't set that folder as the primary location.");

public sealed class SetPrimaryLocationForbiddenException()
    : PrimaryLocationException("You don't have permission to change this item's primary location.");

public sealed class PrimaryLocationConcurrencyException()
    : PrimaryLocationException("This item changed since you loaded it — refresh and try again.");

// A dropped file whose name is already used in the target folder. Its own type rather than the string-message
// ApiActionException it replaces, because this one condition is RECOVERABLE — the caller asks the user what they
// meant (a new version of what is there, or a new document under another name) instead of only reporting it.
// Carries the name so the prompt can name the file without re-deriving it.
public sealed class DocumentNameTakenException(string fileName)
    : ApiActionException($"'{fileName}': a document with that name already exists here.")
{
    public string FileName { get; } = fileName;
}

// Raised by DeleteUserAsync when the user still holds pending review tasks and no replacement reviewer was
// supplied (ADR "Workflow review reassignment") — the caller (Users & groups tab) prompts for a replacement
// and retries with reassignReviewsTo.
public sealed class ReviewerHasPendingReviewsException(string message) : Exception(message);

// Thin HTTP client over the SimplArchive Api (the same endpoints the Blazor client uses). See ADR
// "Cross-platform desktop fat client (Avalonia)" and "Desktop workbench UI".
public sealed class SimplArchiveApiClient
{
    // internal: InboxApi PUTs bytes to a presigned URL too, since the inbox calls moved there (#443).
    internal static readonly HttpClient Anonymous = new();
    private readonly HttpClient _http;
    private InboxApi? _inbox;

    public SimplArchiveApiClient(string accessToken)
    {
        AccessToken = accessToken;
        _http = new HttpClient { BaseAddress = new Uri(DesktopClientOptions.ApiBaseUrl) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    // This client's bearer token — used as the RFC 8693 subject_token to start impersonation (ADR "User
    // impersonation").
    public string AccessToken { get; }

    // Exchanges an admin's access token for an impersonation token representing the target user (ADR "User
    // impersonation"); null if the exchange is refused (e.g. the target is an admin).
    public static async Task<string?> ExchangeImpersonationTokenAsync(string subjectToken, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(DesktopClientOptions.ApiBaseUrl) };
        var response = await http.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = "simplarchive-desktop",
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["requested_subject"] = targetUserId.ToString(),
        }), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("access_token").GetString();
    }

    public sealed record Node(Guid Id, string Name, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, bool OnLegalHold = false, bool CheckedOut = false, bool CheckedOutByMe = false, string CheckedOutByName = "",
        string DocumentType = "", DateOnly? DocumentDate = null, long? SizeBytes = null, IReadOnlyList<string>? Tags = null, string SensitivityLabelName = "", string? SensitivityLabelColor = null, int VersionCount = 0,
        // The latest confirmed version's CreatedAt (filing timestamp) — the "Created" folder contents-sort key
        // (ADR "Per-folder contents sort order"). Null for a folder / version-less doc.
        DateTimeOffset? VersionCreatedAt = null,
        // The item's own sub-resource addresses, as the listing advertised them (ADR 0543, issue #416): a client
        // holding a row follows these instead of composing a path from the document id from a template. Empty only if
        // the row came from somewhere that does not advertise them, in which case a caller must fetch the
        // resource — never rebuild the path.
        IReadOnlyDictionary<string, string>? Links = null)
    {
        /// <summary>The advertised href for <paramref name="rel"/>.</summary>
        /// <remarks>
        /// Throws rather than falling back to a composed path. A rel the server did not advertise means the
        /// action is not available here (ADR 0543) or the row came from a listing that does not advertise it —
        /// either way, rebuilding the URL would paper over the very thing this is replacing, and would do it
        /// silently.
        /// </remarks>
        public string Href(string rel) =>
            Links is not null && Links.TryGetValue(rel, out var href)
                ? href
                : throw new InvalidOperationException(
                    $"The '{rel}' rel was not advertised for '{Name}'. Follow a rel the resource offers, or fetch "
                    + "the resource — do not compose the URL (ADR 0543).");
    }

    // A folder that references a given item, with its full display path — see ADR "References-of-an-item list".
    public sealed record ReferencingFolder(Guid Id, string Name, string Path);

    // The references-of-an-item view: the document's real primary location (null when it's a repository root or
    // the caller can't see the parent) plus the folders that reference it (ADR 0506).
    public sealed record ReferencesView(ReferencingFolder? Primary, IReadOnlyList<ReferencingFolder> Folders);

    // A metadata-search hit — see ADR "Metadata search (first slice)". ParentId is the item's home folder
    // (null = a repository root), for navigating to it.
    // VersionsHref is the address the HIT advertised (#462) — the row carries its own addresses, so previewing a
    // result follows what the listing handed over instead of resolving the document again (ADR 0555/0557). Null
    // for a folder, which advertises no `versions` because it has nothing to preview.
    public sealed record SearchResult(Guid Id, string Name, bool IsFolder, Guid? ParentId, string Path, string Highlight, string? VersionsHref = null, IReadOnlyDictionary<string, string>? Links = null);

    public sealed record IndexField(string FieldName, IReadOnlyList<string> Values);

    public sealed record MaskInfo(Guid? MaskId, string? Name, int? VersionNumber);

    // A tenant mask option for the mask-change dropdown (ADR "Editable mask on the detail pane").
    // SelfHref is the address the mask catalogue advertised for this mask — reading its fields follows that
    // rather than rebuilding /api/masks/{id} from the id beside it (ADR 0543/0555).
    public sealed record MaskOptionInfo(Guid Id, string Name, string? SelfHref = null);

    // A mask's field definition + type, for building the type-aware editor.
    public sealed record MaskFieldInfo(Guid Id, string Name, string DataType, bool IsRequired);

    // System-field values shown always (separate from the mask, ADR "System fields + OCR-language mask
    // field"). Created/CreatedBy/DocumentDate are the currently-shown version's; the OCR-language override +
    // TIFF-source come from the latest TIFF version.
    // DocumentDateHref is the current version's own `document-date` address — the detail pane's Save follows it
    // instead of rebuilding a path out of the two ids beside it (ADR 0543, issue #416).
    public sealed record SystemFields(
        Guid CurrentVersionId, int CurrentVersionNumber, DateTimeOffset CreatedAt, string CreatedByName, string DocumentDate,
        bool HasTiffVersion, string? OcrLanguages, string FileExtension, string? DocumentDateHref = null, string? WorkflowStatus = null);

    public sealed record OcrLanguageOption(string Code, string DisplayName);

    // FileExtension is the current version's derived extension (ADR "Extension off Document.Name"); native
    // Open/Save-as append it to Document.Name (the bare stem) to reconstruct a correct filename.
    public sealed record Preview(string? PreviewUrl, bool PreviewConverted, string? DownloadUrl, string? TextLayoutUrl, string? PreviewPagesUrl, string FileExtension, string? AnnotationsUrl = null);

    // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"). Coordinates are normalized 0..1
    // within each page (top-left origin); the client scales them to the rendered page size.
    public sealed record TextLayoutBox(string Text, double X, double Y, double Width, double Height);

    // A sticky note / positional annotation (ADR "Document annotations"). Etag is the optimistic-concurrency
    // token to send back as If-Match on edit/delete; CanEdit/CanDelete are the server's per-caller hints.
    // Points is the normalized "x,y x,y …" (each 0..1) poly-line for a Freehand (kind 7), null otherwise (ADR 0525).
    public sealed record AnnotationInfo(Guid Id, int PageIndex, int Kind, double PositionX, double PositionY, double? Width, double? Height, string Text, string Color, string AuthorName, string Etag, bool CanEdit, bool CanDelete, string? Points = null);

    public sealed record TextLayoutPageInfo(IReadOnlyList<TextLayoutBox> Words);

    public sealed record TextLayoutInfo(IReadOnlyList<TextLayoutPageInfo> Pages);

    // AuthorCardHref: the "author-card" rel as the server advertised it, or null for a ServiceAccount author
    // (ADR 0543/0544).
    public sealed record Comment(Guid Id, Guid? ParentMessageId, string Body, string AuthorName, DateTimeOffset CreatedAt, string? AuthorCardHref,
        int Kind, int? VersionNumber, string? VersionComment, int? VersionCommentKind,
        // The names behind the body's "@[id]" tokens, resolved by the server (issue #383). The body stores ids,
        // never names, so a rename cannot break a mention.
        IReadOnlyList<Mention> Mentions);

    public sealed record Mention(Guid UserId, string DisplayName);

    public sealed record MentionableUser(Guid Id, string DisplayName);

    public sealed record UserCard(string DisplayName, string Email, bool IsActive, string? PhotoHref);

    /// <summary>A row that carries the addresses its listing advertised (ADR 0543/0555).</summary>
    public interface IAdvertisesLinks
    {
        string Name { get; }

        string? Href(string rel);
    }

    // The per-repository view of a soft-deleted item. Same actions as the tenant-wide row below and therefore
    // the same shape, so restore/purge are written ONCE and take either (CLAUDE.md: one generic, not N copies).
    public sealed record RecycleBinItem(Guid Id, string Name, DateTimeOffset DeletedAt,
        IReadOnlyDictionary<string, string>? Links = null) : IAdvertisesLinks
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // A file entry inside a browsed .zip (ADR "Zip file browsing") — not a real Document.
    // A zip entry. DownloadHref is the address its own row advertised — an entry is not a storage object, so
    // the Api proxies these bytes and the path lives in the server's URL, not in one the client assembles.
    public sealed record ArchiveEntryInfo(string Name, string Path, long Size, string? DownloadHref = null);

    // The signed-in principal's ids + display names (ADR "S3-backed inbox") — names drive the local folder
    // path. IsTenantAdmin gates admin-only actions (e.g. the searchable-PDF backfill).
    public sealed record WhoAmIInfo(Guid? UserId, Guid? TenantId, string? TenantName, string? UserName, bool IsTenantAdmin, bool CanManageUsers, bool HasPhoto, bool CanViewAuditLog, bool MfaEnabled, bool CanResetMfa, bool CanLegalHold, bool CanManageClassification, bool CanOverrideCheckout = false, bool CanImpersonate = false, string? ImpersonatedBy = null, bool CanExport = false, bool CanImport = false, bool CanManageInboxes = false, bool CanManageServiceAccounts = false);

    // Tenant-wide system-level rights, mirroring the User/Group columns (ADR "Users & groups administration
    // tab"). Backs the rights matrix on the Users & groups tab.
    public sealed record SystemRightsData(
        bool IsTenantAdmin, bool CanImpersonate, bool CanOverrideCheckout, bool CanLegalHold,
        bool CanManageClassification, bool CanResetMfa, bool CanManageRepositories, bool CanManageMasks,
        bool CanManageServiceAccounts, bool CanManageUsers, bool CanViewAuditLog, bool CanExport, bool CanImport,
        // Tenant-wide inbox triage (ADR 0532). Defaulted so existing 13-bool construction sites keep compiling.
        bool CanManageInboxes = false,
        // Share a document with someone who has no account (ADR 0546). Defaulted for the same reason.
        bool CanCreateExternalLink = false,
        // Data-classification clearance (ADR "Sensitivity clearance enforcement"). Defaulted so existing
        // construction sites (e.g. a copied-rights bundle) keep compiling.
        int ClearanceRank = 0);

    // A user or group in the combined admin list (ADR "Users & groups administration tab"). IsActive is
    // meaningful only for a user (a group has no active/inactive concept).
    // Links are the row's own advertised addresses (ADR 0543/0555): rights, photo, reset-password, reset-mfa,
    // deactivate for a user; rights, members, delete for a group. The client's methods take this row and follow
    // one of them, instead of rebuilding /users/{id}/… and /groups/{id}/… paths from an id.
    public sealed record PrincipalInfo(bool IsGroup, Guid Id, string Name, bool IsActive, SystemRightsData Rights, bool MfaEnabled = false,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // A machine-to-machine service account (ADR 0203/0534). ClientId is the OAuth client_id; the client_secret is
    // only ever returned once on create/rotate (see NewSecret) and is never carried on a list/read.
    // A REVOKED account advertises none of edit/revoke/rotate-secret, so the row's actions disable from the
    // server's answer rather than from IsActive re-derived here (issue #416).
    public sealed record ServiceAccountInfo(Guid Id, string Name, string ClientId, bool IsActive,
        bool CanManageRepositories, bool CanManageMasks, bool CanManageServiceAccounts, bool CanImport, bool CanExport,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // The one-time client_id + client_secret shown after create/rotate — never retrievable again.
    public sealed record ServiceAccountSecret(string ClientId, string ClientSecret);

    // A server-inbox item — a staged file (ADR "S3-backed inbox"). Download is a presigned URL; HasMask tells
    // whether a `{name}.mask.json` staging sidecar exists (ADR "Inbox item classification + preview"). Group/User
    // label a non-own item's source queue (ADR 0532); MoveUrl is its move action, source query already baked in.
    // Links are the addresses the listing advertised for THIS item — preview, mask, file, move and its own
    // deletion — each already carrying the right source prefix for a group or another user's inbox, which is
    // exactly the part the client used to rebuild by hand (ADR 0543/0555, issue #416).
    public sealed record InboxItemInfo(string Name, long Size, string DownloadUrl, bool HasMask,
        Guid? GroupId = null, string? GroupName = null, Guid? UserId = null, string? UserName = null, string MoveUrl = "",
        IReadOnlyDictionary<string, string>? Links = null, bool Signed = false)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;

        // Own items (no group/user source) get "Send to…"; a group/other-user item gets "Move to my inbox".
        public bool IsOwn => GroupId is null && UserId is null;

        // Appended to the name-based item endpoints (preview / mask) so they resolve against the right source
        // prefix; empty for own items.
        public string SourceQuery => GroupId is { } g ? $"?group={g}" : UserId is { } u ? $"?user={u}" : "";

        // The `GroupName` / `UserName` shown as a source chip; null for own items.
        public string? SourceLabel => GroupName ?? UserName;
    }

    // A destination for the "Send to…" dialog (ADR 0532) — a group the caller belongs to, or another tenant user.
    public sealed record InboxTargetInfo(Guid Id, string Name, bool IsGroup);

    // A staged mask/index-data draft for an inbox item (the `{name}.mask.json` sidecar content). Name +
    // DocumentDate ("yyyy-MM-dd") are the staged system fields (ADR "Staged Name + Document date on inbox items").
    public sealed record InboxMaskDraft(string? Name, string? DocumentDate, Guid? MaskId, IReadOnlyList<InboxMaskFieldValue> Fields, IReadOnlyList<string> OcrLanguages);

    public sealed record InboxMaskFieldValue(Guid FieldDefinitionId, IReadOnlyList<string> Values);

    // A reference (shortcut) filed in a folder — see ADR "Desktop drag-and-drop move and reference".
    // TargetId/Name/HasVersions/HasSubfolders describe the referenced item; ReferenceId identifies the
    // shortcut row (for delete); RealParentId is the target's real home folder (for "Go to …").
    // DeleteHref is the shortcut row's own `delete` address (ADR 0543) — the pair of ids that used to rebuild
    // it are still here because the tree needs them, but nothing composes a URL out of them any more.
    public sealed record Reference(
        Guid ReferenceId, Guid TargetId, string Name, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, Guid? RealParentId,
        string? DeleteHref = null);

    // The approval workflow on a version (ADR "Workflow / document state model", 0009). Status is the
    // WorkflowStatus int; Links maps each valid-transition rel (submit/approve/reject/release) to its href.
    public sealed record WorkflowInfo(
        int Status, string StatusName, string? AssignedToName,
        IReadOnlyList<WorkflowTransitionInfo> History, IReadOnlyDictionary<string, string> Links);

    public sealed record WorkflowTransitionInfo(string ToStatusName, string? AssignedToName, string? PerformedByName, string? RejectionReason);

    // A pending review task assigned to the caller (backs the Tasks tab).
    public sealed record TaskInfo(Guid DocumentId, Guid? ParentId, Guid VersionId, string DocumentName, int? VersionNumber, DateTimeOffset AssignedAt, IReadOnlyDictionary<string, string>? Links = null);

    // A user option for the reviewer picker.
    // RemoveHref is set only where the option came from a collection whose rows advertise a removal address —
    // a group's members; it is null for pickers such as reminder targets (issue #416).
    public sealed record UserOptionInfo(Guid Id, string DisplayName, string? RemoveHref = null);

    // Audit log (ADRs "Audit trail (first slice)" / "... hash chain" / "... retention and purge").
    public sealed record AuditEventInfo(DateTimeOffset Timestamp, string ActorType, string ActorName, string Action, string? TargetType, string? TargetName, string? Details);
    public sealed record AuditPage(IReadOnlyList<AuditEventInfo> Events, string? NextCursor);
    public sealed record AuditVerifyInfo(bool Valid, int CheckedCount, long? BrokenAtSequence);

    public sealed record RepositoryExportOptions(bool ActiveOnly, DateOnly? DocumentDateFrom, DateOnly? DocumentDateTo, DateTimeOffset? FiledFrom, DateTimeOffset? FiledTo, string? CreatedBy, bool IncludePermissions = false);

    public sealed record ImportResultInfo(Guid RootId, string RootName, int Documents, int Versions, int Skipped);
    public sealed record AuditRetentionInfo(int RetentionDays, long ChainStartSequence, DateTimeOffset? LastPurgedAt);
    public sealed record AuditPurgeInfo(int PurgedCount, long ChainStartSequence);

    // A page of audit events, newest first, with optional filters + an opaque cursor for "load more".
    public async Task<AuditPage> GetAuditEventsAsync(string? action, DateTimeOffset? from, DateTimeOffset? to, string? cursor, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.UtcDateTime.ToString("o"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.UtcDateTime.ToString("o"))}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");

        var url = await RootHrefAsync("auditEvents", cancellationToken) + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        var page = await _http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
        var events = page.TryGetProperty("events", out var array)
            ? array.EnumerateArray().Select(ParseAuditEvent).ToList()
            : new List<AuditEventInfo>();
        return new AuditPage(events, ExtractCursor(FindLink(page, "next")));
    }

    // Exports the tenant audit log (respecting the filters) as NDJSON bytes (ADR "Audit trail export").
    public async Task<byte[]> ExportAuditEventsAsync(string? action, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (from is { } f) query.Add($"from={Uri.EscapeDataString(f.UtcDateTime.ToString("o"))}");
        if (to is { } t) query.Add($"to={Uri.EscapeDataString(t.UtcDateTime.ToString("o"))}");

        var url = await AuditRelAsync("export", cancellationToken) + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return await _http.GetByteArrayAsync(url, cancellationToken);
    }

    // Exports a repository/folder + subtree to a .zip (ADR "Repository export"). Tenant-admin-only server-side.
    public async Task<byte[]> ExportRepositoryAsync(Guid rootId, RepositoryExportOptions options, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"versions={(options.ActiveOnly ? "active" : "all")}" };
        if (options.DocumentDateFrom is { } df) query.Add($"documentDateFrom={df:yyyy-MM-dd}");
        if (options.DocumentDateTo is { } dt) query.Add($"documentDateTo={dt:yyyy-MM-dd}");
        if (options.FiledFrom is { } ff) query.Add($"filedFrom={Uri.EscapeDataString(ff.UtcDateTime.ToString("o"))}");
        if (options.FiledTo is { } ft) query.Add($"filedTo={Uri.EscapeDataString(ft.UtcDateTime.ToString("o"))}");
        if (!string.IsNullOrWhiteSpace(options.CreatedBy)) query.Add($"createdBy={Uri.EscapeDataString(options.CreatedBy.Trim())}");
        if (options.IncludePermissions) query.Add("includePermissions=true");

        return await _http.GetByteArrayAsync(await DocumentRelAsync(rootId, "export", cancellationToken) + "?" + string.Join("&", query), cancellationToken);
    }

    // Imports an export archive (ADR "Repository import"). targetFolderId == null → a new repository; otherwise
    // grafted under that folder. Tenant-admin-only server-side. Returns the imported root's name + counts.
    public async Task<ImportResultInfo> ImportRepositoryAsync(Guid? targetFolderId, byte[] zip, bool updateExisting = false, bool includePermissions = false, bool merge = false, string leafConflict = "rename", CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "import.zip");

        // Into a folder → the folder's own `import` rel; a brand-new repository → the one the repositories
        // COLLECTION advertises, since the archive's root becomes a sibling of everything in it and belongs to
        // no repository in particular. `?limit=1` so learning one address doesn't drag back a page of
        // ACL-filtered repositories (ADR 0543, issue #416).
        var basePath = targetFolderId is { } id
            ? await DocumentRelAsync(id, "import", cancellationToken)
            : RequireRel(
                await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("repositories", cancellationToken) + "?limit=1", cancellationToken),
                "import",
                "The repositories collection");
        var url = $"{basePath}?updateExisting={(updateExisting ? "true" : "false")}&includePermissions={(includePermissions ? "true" : "false")}&merge={(merge ? "true" : "false")}&leafConflict={leafConflict}";
        var response = await _http.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new ImportResultInfo(
            json.GetProperty("rootId").GetGuid(),
            json.GetProperty("rootName").GetString() ?? "",
            json.GetProperty("documents").GetInt32(),
            json.GetProperty("versions").GetInt32(),
            json.GetProperty("skipped").GetInt32());
    }

    public async Task<AuditVerifyInfo> VerifyAuditChainAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await AuditRelAsync("verify", cancellationToken), cancellationToken);
        return new AuditVerifyInfo(
            json.GetProperty("valid").GetBoolean(),
            json.GetProperty("checkedCount").GetInt32(),
            json.TryGetProperty("brokenAtSequence", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null);
    }

    public sealed record WormVerifyInfo(bool Valid, int SegmentCount, int CheckedCount, long? BrokenAtSequence, string? Reason);

    // Verifies the sealed WORM segments against the DB (ADR "Audit WORM segment verify").
    public async Task<WormVerifyInfo> VerifyAuditWormAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await AuditRelAsync("worm-verify", cancellationToken), cancellationToken);
        return new WormVerifyInfo(
            json.GetProperty("valid").GetBoolean(),
            json.GetProperty("segmentCount").GetInt32(),
            json.GetProperty("checkedCount").GetInt32(),
            json.TryGetProperty("brokenAtSequence", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null,
            json.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null);
    }

    // Retention, export, verify, worm-verify and purge are all rels ON the audit-events collection (issue
    // #416), so reaching any of them means reading that collection — and pulling a page of audit events to
    // learn one address is the "two round trips, one of them large" trap. `?limit=1` on the advertised href
    // keeps the read trivial while the address still comes from the server: a query on a rel's href, not a
    // path this client invented.
    //
    // Cached like the API root's own rels, and for the same reason: these five do not change between calls,
    // and the audit tab would otherwise re-read the collection once per button.
    private async Task<string> AuditRelAsync(string rel, CancellationToken cancellationToken)
    {
        if (_auditLinks is null)
        {
            await _auditGate.WaitAsync(cancellationToken);
            try
            {
                if (_auditLinks is null)
                {
                    var href = await RootHrefAsync("auditEvents", cancellationToken);
                    var page = await _http.GetFromJsonAsync<JsonElement>($"{href}?limit=1", cancellationToken);
                    _auditLinks = ParseLinks(page) ?? new Dictionary<string, string>();
                }
            }
            finally
            {
                _auditGate.Release();
            }
        }

        return _auditLinks.TryGetValue(rel, out var relHref)
            ? relHref
            : throw new InvalidOperationException($"The audit log advertised no '{rel}' rel (ADR 0543).");
    }

    private IReadOnlyDictionary<string, string>? _auditLinks;
    private readonly SemaphoreSlim _auditGate = new(1, 1);

    private Task<string> AuditRetentionHrefAsync(CancellationToken cancellationToken) =>
        AuditRelAsync("retention", cancellationToken);

    public async Task<AuditRetentionInfo> GetAuditRetentionAsync(CancellationToken cancellationToken = default) =>
        ParseRetention(await _http.GetFromJsonAsync<JsonElement>(await AuditRetentionHrefAsync(cancellationToken), cancellationToken));

    public async Task<AuditRetentionInfo> SetAuditRetentionAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(await AuditRetentionHrefAsync(cancellationToken), new { retentionDays }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to change audit retention.");
        }

        response.EnsureSuccessStatusCode();
        return ParseRetention(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task<AuditPurgeInfo> PurgeAuditAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await AuditRelAsync("purge", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to purge the audit log.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new AuditPurgeInfo(json.GetProperty("purgedCount").GetInt32(), json.GetProperty("chainStartSequence").GetInt64());
    }

    private static AuditEventInfo ParseAuditEvent(JsonElement e) => new(
        e.GetProperty("timestamp").GetDateTimeOffset(),
        e.TryGetProperty("actorType", out var at) ? at.GetString() ?? "" : "",
        e.TryGetProperty("actorName", out var an) ? an.GetString() ?? "" : "",
        e.TryGetProperty("action", out var ac) ? ac.GetString() ?? "" : "",
        StrOrNull(e, "targetType"),
        StrOrNull(e, "targetName"),
        StrOrNull(e, "details"));

    private static AuditRetentionInfo ParseRetention(JsonElement json) => new(
        json.GetProperty("retentionDays").GetInt32(),
        json.GetProperty("chainStartSequence").GetInt64(),
        json.TryGetProperty("lastPurgedAt", out var lp) && lp.ValueKind == JsonValueKind.String ? lp.GetDateTimeOffset() : null);

    // Pulls the cursor value out of a "next" hypermedia href (…?cursor=…&limit=…).
    private static string? ExtractCursor(string? nextHref)
    {
        if (string.IsNullOrEmpty(nextHref))
        {
            return null;
        }

        var index = nextHref.IndexOf("cursor=", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var value = nextHref[(index + "cursor=".Length)..];
        var amp = value.IndexOf('&');
        return Uri.UnescapeDataString(amp >= 0 ? value[..amp] : value);
    }

    public async Task<List<Node>> GetRepositoriesAsync(CancellationToken cancellationToken = default) =>
        await LoadPagedAsync(await RootHrefAsync("repositories", cancellationToken), "repositories", ParseNode, cancellationToken);

    public sealed record AdminPersonalRepoInfo(Guid UserId, string DisplayName, string Email, bool UserIsActive, Guid RepositoryId, bool HasChildren, bool HasSubfolders);

    // Lists every user's personal repository (ADR "Tenant-admin Administration → Users view") — tenant-admin only.
    public async Task<List<AdminPersonalRepoInfo>> GetAdminPersonalRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        // The root's `admin` rel leads to the administration index, which advertises this list — two hops, but
        // both of them followed rather than assembled, and paid once per admin screen (ADR 0543).
        var admin = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("admin", cancellationToken), cancellationToken);
        var json = await _http.GetFromJsonAsync<JsonElement>(RequireRel(admin, "personal-repositories", "The administration index"), cancellationToken);
        var list = new List<AdminPersonalRepoInfo>();
        if (json.TryGetProperty("repositories", out var array))
        {
            foreach (var r in array.EnumerateArray())
            {
                list.Add(new AdminPersonalRepoInfo(
                    r.GetProperty("userId").GetGuid(),
                    r.GetProperty("displayName").GetString() ?? "",
                    r.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    r.TryGetProperty("userIsActive", out var a) && a.GetBoolean(),
                    r.GetProperty("repositoryId").GetGuid(),
                    r.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
                    r.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean()));
            }
        }
        return list;
    }

    // Get-or-create the current user's personal repository (ADR "Per-user personal repository"). Returns null if
    // the caller has no personal space (e.g. a ServiceAccount → 403) so the tree still renders shared repositories.
    public async Task<Node?> GetPersonalRepositoryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await MeHrefAsync("personalRepository", cancellationToken), null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new Node(
            json.GetProperty("id").GetGuid(),
            json.GetProperty("name").GetString() ?? "Personal",
            json.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
            HasVersions: false,
            json.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
            HasReferences: false,
            // The resource advertises `children` — carry it, or the Personal tree node has no address to expand
            // by and Href() throws (ADR 0543). Hand-built Nodes are exactly where this is easy to forget, which
            // is what DesktopListingRelsTests now guards.
            Links: ParseLinks(json));
    }

    // Takes the advertised href (node.Href("children")), not a folder id (ADR 0543, issue #416). Every listing
    // that can produce a row here advertises it — the children listing and the repositories listing both do.
    public Task<List<Node>> GetChildrenAsync(string childrenHref, CancellationToken cancellationToken = default) =>
        LoadPagedAsync(childrenHref, "children", ParseNode, cancellationToken);

    /// <summary>
    /// A folder's contents AND its persisted contents order, from the one listing that already carries both.
    /// Following rels must not turn one screen into N requests, and the order travelling in the children
    /// envelope is precisely so a client does not have to ask for it separately (ADR 0543, issue #416).
    /// </summary>
    public async Task<(List<Node> Children, int SortOrder)> GetFolderContentsAsync(string childrenHref, CancellationToken cancellationToken = default)
    {
        var sortOrder = 1;
        var first = true;
        var children = await LoadPagedAsync(childrenHref, "children", ParseNode, cancellationToken, page =>
        {
            if (first)
            {
                sortOrder = ReadContentsSortOrder(page);
                first = false;
            }
        });

        return (children, sortOrder);
    }

    // For a caller that has only an ID and no resource — a breadcrumb, a restored selection, a self-test — this
    // FETCHES the document and follows its own `children` rel. One round trip, then a rel; never a composed
    // sub-resource path.
    //
    // Prefer the href overload wherever a row or node is in hand — this exists so the remaining id-shaped call
    // sites (the view model tracks "where am I" as a Guid) do not have to rebuild sub-resource paths while that
    // state is still id-shaped.
    public async Task<List<Node>> GetChildrenAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        await GetChildrenAsync(await DocumentRelAsync(documentId, "children", cancellationToken), cancellationToken);

    /// <summary>
    /// Every address a document advertises, from ONE read (ADR 0543/0555). For a caller that holds an id and
    /// needs several of the document's sub-resources at once — opening a folder wants children, references and
    /// the contents order — this is what keeps "follow a rel" from meaning "fetch the document once per rel".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetDocumentLinksAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        ParseLinks(await _http.GetFromJsonAsync<JsonElement>(DocumentAddress(documentId), cancellationToken))
        ?? throw new InvalidOperationException($"Document {documentId} advertised no links at all (ADR 0543).");

    /// <summary>The essentials of a document reached by ADDRESS — what a cross-tab open needs in one read.</summary>
    public sealed record DocumentStub(Guid Id, string Name, IReadOnlyDictionary<string, string> Links);

    /// <summary>
    /// A document by its ADVERTISED address (#443): id, name and rels from one GET. This is what a payload-row
    /// consumer (a task, a notification, a reminder, a search hit) uses to open the folder its row named —
    /// following the row's `parent`/`document` rel instead of handing a bare id back into the address turn.
    /// One request where the id path cost two (a name fetch plus a links fetch).
    /// </summary>
    public async Task<DocumentStub> GetDocumentByAddressAsync(string documentHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(documentHref, cancellationToken);
        return new DocumentStub(
            json.GetProperty("id").GetGuid(),
            json.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            ParseLinks(json) ?? throw new InvalidOperationException($"'{documentHref}' advertised no links at all (ADR 0543)."));
    }

    // The item's ancestor folder ids, repository-root first down to its immediate parent (issue #340) — used to
    // reveal a search hit in the lazy tree. Empty for an item filed at a repository root.
    public async Task<List<Guid>> GetAncestorsAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "ancestors", cancellationToken), cancellationToken);
        var ids = new List<Guid>();
        if (json.TryGetProperty("ancestors", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (a.TryGetProperty("id", out var idEl) && idEl.TryGetGuid(out var id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    // The folder's persisted default contents sort order (ADR "Per-folder contents sort order") from the children
    // listing envelope — 0=Name / 1=DocumentDate / 2=Created; DocumentDate (1) when unavailable.
    //
    // The order travels IN the children envelope, so a screen that is listing the folder anyway should call
    // GetFolderContentsAsync and read both from one response. This overload is for the callers that want only
    // the number (a VM check), and it asks for a single row rather than a page to get it.
    public async Task<int> GetContentsSortOrderAsync(Guid folderId, CancellationToken cancellationToken = default) =>
        await GetContentsSortOrderAsync(await DocumentRelAsync(folderId, "children", cancellationToken), cancellationToken);

    public async Task<int> GetContentsSortOrderAsync(string childrenHref, CancellationToken cancellationToken = default) =>
        ReadContentsSortOrder(await _http.GetFromJsonAsync<JsonElement>(childrenHref + "?limit=1", cancellationToken));

    private static int ReadContentsSortOrder(JsonElement envelope) =>
        envelope.TryGetProperty("contentsSortOrder", out var so) && so.ValueKind == JsonValueKind.Number ? so.GetInt32() : 1;

    // Sets the folder's persisted default contents sort order (CanEditIndexData-gated).
    public async Task SetContentsSortOrderAsync(Guid folderId, int sortOrder, CancellationToken cancellationToken = default)
    {
        // Fetch-then-follow: a user-initiated write on one item, so one extra request when acting is the right
        // trade against composing the path (ADR 0543, #416).
        var response = await _http.PutAsJsonAsync(await DocumentRelAsync(folderId, "contents-sort-order", cancellationToken), new { sortOrder }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the contents sort order ({(int)response.StatusCode}).");
        }
    }

    // Lists a .zip document's entries on demand (ADR "Zip file browsing") — nothing is unpacked.
    public async Task<IReadOnlyList<ArchiveEntryInfo>> GetArchiveEntriesAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        // Follows the resource's own archive-entries rel — advertised only for a zip, so its PRESENCE is the
        // server answering "can I browse inside this?" instead of the client comparing ".zip" (#416).
        var json = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "archive-entries", cancellationToken), cancellationToken);
        var entries = new List<ArchiveEntryInfo>();
        if (json.TryGetProperty("entries", out var array))
        {
            foreach (var e in array.EnumerateArray())
            {
                entries.Add(new ArchiveEntryInfo(
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("path").GetString() ?? "",
                    e.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    RelHref(e, "download")));
            }
        }

        return entries;
    }

    // Downloads one archive entry's bytes at the address its row advertised (ADR 0543/0555).
    public Task<byte[]> DownloadArchiveEntryAsync(string downloadHref, CancellationToken cancellationToken = default) =>
        _http.GetByteArrayAsync(downloadHref, cancellationToken);

    public async Task<WhoAmIInfo> GetWhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("whoami", cancellationToken), cancellationToken);
        return new WhoAmIInfo(
            json.TryGetProperty("userId", out var u) && u.ValueKind == JsonValueKind.String ? u.GetGuid() : null,
            json.TryGetProperty("tenantId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetGuid() : null,
            json.TryGetProperty("tenantName", out var tn) ? tn.GetString() : null,
            json.TryGetProperty("userName", out var un) ? un.GetString() : null,
            json.TryGetProperty("isTenantAdmin", out var a) && a.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageUsers", out var m) && m.ValueKind == JsonValueKind.True,
            json.TryGetProperty("hasPhoto", out var hp) && hp.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canViewAuditLog", out var av) && av.ValueKind == JsonValueKind.True,
            json.TryGetProperty("mfaEnabled", out var mfa) && mfa.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canResetMfa", out var crm) && crm.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canLegalHold", out var clh) && clh.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageClassification", out var cmc) && cmc.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canOverrideCheckout", out var coc) && coc.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canImpersonate", out var ci) && ci.ValueKind == JsonValueKind.True,
            json.TryGetProperty("impersonatedBy", out var ib) && ib.ValueKind == JsonValueKind.String ? ib.GetString() : null,
            json.TryGetProperty("canExport", out var ce) && ce.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canImport", out var cim) && cim.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageInboxes", out var cmi) && cmi.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageServiceAccounts", out var cmsa) && cmsa.ValueKind == JsonValueKind.True);
    }

    // The count of existing "current TIFF" documents with no searchable-PDF successor yet (ADR "Backfill
    // searchable PDFs for existing TIFFs").
    public async Task<int> GetTiffBackfillPendingAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("searchablePdfBackfill", cancellationToken), cancellationToken);
        return json.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
    }

    // Enqueues a searchable-PDF conversion for every current TIFF; returns how many were enqueued.
    public async Task<int> TriggerTiffBackfillAsync(CancellationToken cancellationToken = default)
    {
        // Same address as the status read above — the root advertises it once and both verbs follow it.
        var response = await _http.PostAsync(await RootHrefAsync("searchablePdfBackfill", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
    }

    /// <summary>The inbox's own api surface (ADR 0575) — its listing and its page operations.</summary>
    public InboxApi Inbox => _inbox ??= new InboxApi(_http, this);

    // The caller's effective group inboxes (ADR 0532) — the "Send to a group" choices.
    public async Task<IReadOnlyList<InboxTargetInfo>> GetInboxGroupsAsync(CancellationToken cancellationToken = default) =>
        await GetInboxTargetsAsync(await RootHrefAsync("inboxGroups", cancellationToken), "groups", isGroup: true, cancellationToken);

    // The other active tenant users (ADR 0532) — the "Send to a user" choices, and the admin user-picker list.
    public async Task<IReadOnlyList<InboxTargetInfo>> GetInboxUsersAsync(CancellationToken cancellationToken = default) =>
        await GetInboxTargetsAsync(await RootHrefAsync("inboxUsers", cancellationToken), "users", isGroup: false, cancellationToken);

    private async Task<IReadOnlyList<InboxTargetInfo>> GetInboxTargetsAsync(string url, string arrayProp, bool isGroup, CancellationToken cancellationToken)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
        var targets = new List<InboxTargetInfo>();
        if (json.TryGetProperty(arrayProp, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in array.EnumerateArray())
            {
                targets.Add(new InboxTargetInfo(t.GetProperty("id").GetGuid(), t.GetProperty("name").GetString() ?? "", isGroup));
            }
        }

        return targets;
    }

    // Moves an inbox item into another inbox (ADR 0532): exactly one target — a group or a user. moveUrl is the
    // item's server-built move action (its source `?group=`/`?user=` already baked in).
    public async Task MoveInboxItemAsync(string moveUrl, Guid? targetGroupId, Guid? targetUserId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(moveUrl, new { targetGroupId, targetUserId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to move that item there.");
        }

        response.EnsureSuccessStatusCode();
    }

    // The inbox item's preview (renditions on the object key) — same Preview shape as a document's, so it feeds
    // the same rendering + hit-overlay pipeline. 204 (no preview available) yields an all-null Preview.
    public async Task<Preview> GetInboxPreviewAsync(InboxItemInfo item, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(RequireHref(item, "preview"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
        {
            return new Preview(null, false, null, null, null, "");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        string? Link(string rel) => json.TryGetProperty("links", out var links)
            ? links.EnumerateArray().Where(l => l.GetProperty("rel").GetString() == rel).Select(l => l.GetProperty("href").GetString()).FirstOrDefault()
            : null;

        return new Preview(
            json.TryGetProperty("previewUrl", out var pu) ? pu.GetString() : null,
            json.TryGetProperty("previewConverted", out var pc) && pc.GetBoolean(),
            DownloadUrl: null,
            Link("text-layout"),
            Link("preview-pages"),
            System.IO.Path.GetExtension(item.Name));
    }

    // Reads an inbox item's staged mask/index-data draft (the `{name}.mask.json` sidecar); MaskId null = none.
    public async Task<InboxMaskDraft> GetInboxMaskAsync(InboxItemInfo item, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(RequireHref(item, "mask"), cancellationToken);
        return ParseInboxMaskDraft(json);
    }

    // Parses the `{maskId, fields:[{fieldDefinitionId, values}]}` draft shape (the server response and the local
    // sidecar file share it, so a moved item carries its staged mask both ways).
    public static InboxMaskDraft ParseInboxMaskDraft(JsonElement json)
    {
        var name = json.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
        var docDate = json.TryGetProperty("documentDate", out var dd) && dd.ValueKind == JsonValueKind.String ? dd.GetString() : null;
        var maskId = json.TryGetProperty("maskId", out var mid) && mid.ValueKind == JsonValueKind.String ? mid.GetGuid() : (Guid?)null;
        var fields = new List<InboxMaskFieldValue>();
        if (json.TryGetProperty("fields", out var fieldArray) && fieldArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fieldArray.EnumerateArray())
            {
                var values = f.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : [];
                fields.Add(new InboxMaskFieldValue(f.GetProperty("fieldDefinitionId").GetGuid(), values));
            }
        }

        var ocrLanguages = json.TryGetProperty("ocrLanguages", out var oc) && oc.ValueKind == JsonValueKind.Array
            ? oc.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];
        return new InboxMaskDraft(name, docDate, maskId, fields, ocrLanguages);
    }

    // Writes (or, when nothing is staged, clears) an inbox item's staged mask/index-data draft. Name +
    // documentDate ("yyyy-MM-dd", or null) are the staged system fields.
    public async Task SetInboxMaskAsync(InboxItemInfo item, string? stagedName, string? documentDate, Guid? maskId,
        IEnumerable<(Guid FieldDefinitionId, IReadOnlyList<string> Values)> fields, IReadOnlyList<string>? ocrLanguages = null, CancellationToken cancellationToken = default)
    {
        var body = new { name = stagedName, documentDate, maskId, fields = fields.Select(f => new { fieldDefinitionId = f.FieldDefinitionId, values = f.Values }), ocrLanguages = ocrLanguages is { Count: > 0 } o ? o : null };
        (await _http.PutAsJsonAsync(RequireHref(item, "mask"), body, cancellationToken)).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Copies a repository document into the caller's inbox as a template, carrying its mask and index values
    /// (#467). The copy happens server-side, so no bytes travel through the client.
    /// </summary>
    /// <remarks>
    /// Reached by FOLLOWING the inbox listing's <c>from-document</c> rel rather than composing the path — the
    /// desktop client's burn-down is finished and its one named exception is elsewhere (ADR 0543, #443).
    /// </remarks>
    public async Task CopyDocumentToInboxAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var inbox = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("inbox", cancellationToken), cancellationToken);
        var href = inbox.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "from-document")
            .GetProperty("href").GetString()
            ?? throw new ApiActionException("The inbox did not offer a template copy here.");

        using var response = await _http.PostAsJsonAsync(href, new { documentId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("Your inbox already holds an item with that name, or the document has no version to copy.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task FileInboxItemAsync(InboxItemInfo item, Guid folderId, string? comment = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(item, "file"), new { folderId, comment }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to file into that folder.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Files the inbox item as a new version of an existing document (ADR "Context-aware inbox filing dialog").
    public async Task FileInboxItemAsVersionAsync(InboxItemInfo item, Guid documentId, string? comment = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(item, "file"), new { documentId, comment }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to add a version to that document.");
        }

        response.EnsureSuccessStatusCode();
    }

    // The item's OWN address, which the listing advertises as `self` with DELETE as its method.
    public Task DeleteInboxItemAsync(InboxItemInfo item, CancellationToken cancellationToken = default) =>
        _http.DeleteAsync(RequireHref(item, "self"), cancellationToken);

    public async Task<string> GetDocumentNameAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        (await GetDocumentDetailAsync(documentId, cancellationToken)).Name;

    public async Task<MaskInfo> GetMaskAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var mask = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "mask", cancellationToken), cancellationToken);
        return new MaskInfo(
            mask.TryGetProperty("maskId", out var mid) && mid.ValueKind == JsonValueKind.String ? mid.GetGuid() : null,
            mask.TryGetProperty("name", out var n) ? n.GetString() : null,
            mask.TryGetProperty("versionNumber", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null);
    }

    // The tenant's masks (id + name) for the mask-change dropdown (ADR "Editable mask on the detail pane").
    public async Task<IReadOnlyList<MaskOptionInfo>> GetMasksAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("masks", cancellationToken), cancellationToken);
        var list = new List<MaskOptionInfo>();
        if (json.TryGetProperty("masks", out var masks))
        {
            foreach (var m in masks.EnumerateArray())
            {
                list.Add(new MaskOptionInfo(m.GetProperty("id").GetGuid(), m.GetProperty("name").GetString() ?? "", RelHref(m, "self")));
            }
        }

        return list;
    }

    // A mask's field definitions (+ types), for building the type-aware editors.
    public async Task<IReadOnlyList<MaskFieldInfo>> GetMaskFieldsAsync(MaskOptionInfo mask, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(
            mask.SelfHref ?? throw new InvalidOperationException($"The mask '{mask.Name}' advertised no 'self' rel (ADR 0543/0555)."),
            cancellationToken);
        var list = new List<MaskFieldInfo>();
        if (json.TryGetProperty("fields", out var fields))
        {
            foreach (var f in fields.EnumerateArray())
            {
                list.Add(new MaskFieldInfo(
                    f.GetProperty("id").GetGuid(),
                    f.GetProperty("name").GetString() ?? "",
                    f.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "Text" : "Text",
                    f.TryGetProperty("isRequired", out var r) && r.GetBoolean()));
            }
        }

        return list;
    }

    // Assigns (or changes) the document's mask. 400 REQUIRED_FIELD_MISSING surfaces as a friendly message.
    public async Task SetMaskAsync(Guid documentId, Guid maskId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync(await DocumentRelAsync(documentId, "mask", cancellationToken), new { maskId }, cancellationToken);
        await ThrowIfProblemAsync(response, "Could not assign the mask", cancellationToken);
    }

    public async Task ClearMaskAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync(await DocumentRelAsync(documentId, "mask", cancellationToken), cancellationToken);
        await ThrowIfProblemAsync(response, "Could not clear the mask", cancellationToken);
    }

    // Replaces the whole index-data set. 400 FIELD_VALUE_INVALID / MULTIPLE_VALUES_NOT_ALLOWED surface as a message.
    public async Task SetIndexDataAsync(Guid documentId, IEnumerable<(Guid FieldDefinitionId, IReadOnlyList<string> Values)> fields, CancellationToken cancellationToken = default)
    {
        var body = new { fields = fields.Select(f => new { fieldDefinitionId = f.FieldDefinitionId, values = f.Values }) };
        var response = await _http.PutAsJsonAsync(await DocumentRelAsync(documentId, "index-data", cancellationToken), body, cancellationToken);
        await ThrowIfProblemAsync(response, "Could not save the index data", cancellationToken);
    }

    // Turns a failed response into an ApiActionException carrying text the USER can read, in their language.
    //
    // Reads `errorCode`, not `detail`. The detail is English — the API's 153 exception classes carry their
    // message as a constructor literal, so no Accept-Language handling reaches them — and this method is on the
    // path of every failed call in the desktop, which made it the single biggest source of English in an
    // otherwise German UI (issue #424). The code is the stable, language-neutral contract (ADR 0543), so it
    // crosses the wire and ApiErrorText supplies the words.
    internal static async Task ThrowIfProblemAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? errorCode = null;
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (json.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code)
            {
                errorCode = code;
            }
        }
        catch
        {
            // No problem body at all (a proxy error page, a connection reset) — fall back to the caller's message,
            // which is already localised at its call site.
            throw new ApiActionException(fallback);
        }

        throw new ApiActionException(errorCode is null ? fallback : ApiErrorText.For(errorCode));
    }

    public async Task<List<IndexField>> GetIndexDataAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "index-data", cancellationToken), cancellationToken);
        var fields = new List<IndexField>();
        if (response.TryGetProperty("fields", out var items))
        {
            foreach (var field in items.EnumerateArray())
            {
                var values = field.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array
                    ? vs.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                fields.Add(new IndexField(field.GetProperty("fieldName").GetString() ?? "", values));
            }
        }

        return fields;
    }

    // The always-shown system fields (ADR "System fields + OCR-language mask field"): Created/CreatedBy/
    // DocumentDate from the latest confirmed version; the OCR-language override + whether a TIFF source exists
    // from the latest confirmed TIFF version.
    // The document's current version JsonElement honoring the server's currentVersionId pointer (ADR
    // "Version-restore via a current-version pointer", issue #265), else the latest confirmed. Returns the
    // element + its version number, or null when there's no confirmed version.
    private static (JsonElement Version, int Number)? PickCurrentVersionElement(JsonElement response)
    {
        if (!response.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Guid? pointer = response.TryGetProperty("currentVersionId", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetGuid() : null;
        JsonElement? latest = null, pinned = null;
        int latestNumber = -1, pinnedNumber = -1;
        foreach (var v in versions.EnumerateArray())
        {
            if (v.GetProperty("status").GetString() != "Confirmed")
            {
                continue;
            }

            var number = v.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : 0;
            if (number >= latestNumber) { latestNumber = number; latest = v; }
            if (pointer is { } p && v.GetProperty("id").GetGuid() == p) { pinned = v; pinnedNumber = number; }
        }

        if (pinned is { } pv) return (pv, pinnedNumber);
        if (latest is { } lv) return (lv, latestNumber);
        return null;
    }

    public async Task<SystemFields?> GetSystemFieldsAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "versions", cancellationToken), cancellationToken);
        if (PickCurrentVersionElement(response) is not { } picked)
        {
            return null;
        }

        var cur = picked.Version;
        var currentNumber = picked.Number;

        // The latest TIFF version — the OCR source, a separate concept from "current".
        JsonElement? tiff = null;
        var tiffNumber = -1;
        if (response.TryGetProperty("versions", out var versions))
        {
            foreach (var v in versions.EnumerateArray())
            {
                if (v.GetProperty("status").GetString() != "Confirmed")
                {
                    continue;
                }

                var number = v.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : 0;
                var objectKey = v.TryGetProperty("objectKey", out var ok) ? ok.GetString() ?? "" : "";
                if ((objectKey.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || objectKey.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)) && number >= tiffNumber)
                {
                    tiffNumber = number;
                    tiff = v;
                }
            }
        }

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";

        string? ocr = null;
        if (tiff is { } t && t.TryGetProperty("ocrLanguages", out var o) && o.ValueKind == JsonValueKind.String)
        {
            ocr = o.GetString();
        }

        return new SystemFields(
            cur.GetProperty("id").GetGuid(),
            currentNumber,
            cur.TryGetProperty("createdAt", out var ca) ? ca.GetDateTimeOffset() : default,
            Str(cur, "createdByName"),
            Str(cur, "documentDate"),
            tiff is not null,
            ocr,
            Str(cur, "fileExtension"),
            RelHref(cur, "document-date"), StrOrNull(cur, "workflowStatus"));
    }

    public async Task<IReadOnlyList<OcrLanguageOption>> GetOcrLanguageCatalogAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("ocrLanguages", cancellationToken), cancellationToken);
        var list = new List<OcrLanguageOption>();
        if (json.TryGetProperty("languages", out var langs))
        {
            foreach (var l in langs.EnumerateArray())
            {
                list.Add(new OcrLanguageOption(l.GetProperty("code").GetString() ?? "", l.GetProperty("displayName").GetString() ?? ""));
            }
        }

        return list;
    }

    // Sets the document's OCR-language override (ordered codes) and re-runs the searchable-PDF conversion.
    public async Task SetOcrLanguagesAsync(Guid documentId, IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync(await DocumentRelAsync(documentId, "ocr-languages", cancellationToken), new { languages = codes }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set OCR languages ({(int)response.StatusCode}).");
        }
    }

    // Data-classification / sensitivity label (ADR "Configurable sensitivity labels + upload defaults") — the
    // per-tenant label on the document (id/name/colour + whether it watermarks), read from the document resource.
    public sealed record DocumentSensitivityInfo(Guid? LabelId, string Name, string? Color, bool Watermark);

    // SelfHref / RetireHref / UnretireHref are the addresses the catalog row advertised. Exactly one of the last
    // two is present, and which one it is expresses the label's state (ADR 0543, issue #416).
    public sealed record SensitivityLabelInfo(Guid Id, string Name, int Rank, string? Color, bool Watermark, bool Retired,
        string? SelfHref = null, string? RetireHref = null, string? UnretireHref = null);
    public sealed record SensitivityLabelCatalog(IReadOnlyList<SensitivityLabelInfo> Items, bool CanManage);

    public async Task<DocumentSensitivityInfo> GetDocumentSensitivityAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        (await GetDocumentDetailAsync(documentId, cancellationToken)).Sensitivity;

    // Everything the detail pane needs from the document resource, from ONE read of it (issue #385).
    //
    // The name and the sensitivity label used to be two separate GETs of the same URL, which is why the
    // per-document external-links dialog had nowhere to get its href from without composing one: the rel is
    // advertised on this resource, and ADR 0543 forbids rebuilding the URL instead of following it. Parsing the
    // resource once, here, is what makes the rel reachable.
    // ContentsSortOrder is meaningful for a FOLDER only. It rides along here because the detail pane for a child
    // folder is opened from its parent's listing, where the child's own setting has never been fetched (#408).
    // Links carries the rels the resource advertised, so a caller that already fetched the detail follows one
    // instead of composing a path (ADR 0543, issue #416). ExternalLinksHref predates this and stays: its ABSENCE
    // is meaningful (tenant switch off, or a folder), which is a different question from "what is its address".
    public sealed record DocumentDetailInfo(string Name, DocumentSensitivityInfo Sensitivity, string? ExternalLinksHref, int ContentsSortOrder,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        /// <summary>The advertised href for <paramref name="rel"/>; throws rather than composing one.</summary>
        public string Href(string rel) =>
            Links is not null && Links.TryGetValue(rel, out var href)
                ? href
                : throw new InvalidOperationException(
                    $"The '{rel}' rel was not advertised for '{Name}'. Follow a rel the resource offers, or fetch "
                    + "the resource — do not compose the URL (ADR 0543).");
    }

    public async Task<DocumentDetailInfo> GetDocumentDetailAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(DocumentAddress(documentId), cancellationToken);

        return new DocumentDetailInfo(
            json.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            new DocumentSensitivityInfo(
                json.TryGetProperty("sensitivityLabelId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null,
                json.TryGetProperty("sensitivityLabelName", out var n) ? n.GetString() ?? "" : "",
                json.TryGetProperty("sensitivityLabelColor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                json.TryGetProperty("sensitivityWatermark", out var w) && w.ValueKind == JsonValueKind.True),
            // Absent when the tenant has the feature off or the caller may not share this document — a missing
            // rel means "not available to you, here, now", so the affordance is simply not offered (ADR 0543).
            // A FOLDER never carries it: sharing one is refused, so the icon must not appear either.
            RelHref(json, "external-links"),
            json.TryGetProperty("contentsSortOrder", out var so) && so.ValueKind == JsonValueKind.Number ? so.GetInt32() : 0,
            ParseLinks(json));
    }

    public async Task SetSensitivityAsync(Guid documentId, Guid? labelId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync(await DocumentRelAsync(documentId, "sensitivity", cancellationToken), new { labelId }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the sensitivity label ({(int)response.StatusCode}).");
        }
    }

    // The tenant's configurable label catalog (for the picker + admin).
    public async Task<SensitivityLabelCatalog> GetSensitivityLabelsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("sensitivityLabels", cancellationToken), cancellationToken);
        var items = new List<SensitivityLabelInfo>();
        if (json.TryGetProperty("labels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in arr.EnumerateArray())
            {
                var links = ParseLinks(l) ?? new Dictionary<string, string>();
                items.Add(new SensitivityLabelInfo(
                    l.GetProperty("id").GetGuid(),
                    l.GetProperty("name").GetString() ?? "",
                    l.TryGetProperty("rank", out var r) ? r.GetInt32() : 0,
                    l.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                    l.TryGetProperty("watermark", out var w) && w.ValueKind == JsonValueKind.True,
                    l.TryGetProperty("retired", out var rt) && rt.ValueKind == JsonValueKind.True,
                    links.GetValueOrDefault("self"),
                    // Exactly one of these is advertised, and which one IS the label's state — the client no
                    // longer decides "retire or un-retire?" from the Retired flag (issue #416).
                    links.GetValueOrDefault("retire"),
                    links.GetValueOrDefault("unretire")));
            }
        }

        return new SensitivityLabelCatalog(items, json.TryGetProperty("canManage", out var cm) && cm.GetBoolean());
    }

    public async Task CreateSensitivityLabelAsync(string name, int rank, string? color, bool watermark, CancellationToken cancellationToken = default)
    {
        var resp = await _http.PostAsJsonAsync(await RootHrefAsync("sensitivityLabels", cancellationToken), new { name, rank, color, watermark }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await ErrorMessageAsync(resp, "Could not add the label."));
    }

    /// <summary>Updates a label at the address its own catalog row advertised (`self`).</summary>
    public async Task UpdateSensitivityLabelAsync(string selfHref, string name, int rank, string? color, bool watermark, CancellationToken cancellationToken = default)
    {
        var resp = await _http.PutAsJsonAsync(selfHref, new { name, rank, color, watermark }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await ErrorMessageAsync(resp, "Could not update the label."));
    }

    public async Task RetireSensitivityLabelAsync(string retireHref, CancellationToken cancellationToken = default) =>
        (await _http.DeleteAsync(retireHref, cancellationToken)).EnsureSuccessStatusCode();

    public async Task UnretireSensitivityLabelAsync(string unretireHref, CancellationToken cancellationToken = default) =>
        (await _http.PostAsync(unretireHref, null, cancellationToken)).EnsureSuccessStatusCode();

    // Free-form tags (ADR "Document tags"). GET the document's tags; PUT-replaces the whole set (the server
    // normalizes/dedupes and returns the stored set); the tenant tag catalog backs add-box autocomplete.
    // Takes the advertised href (detail.Href("tags")), not a document id (ADR 0543, issue #416).
    public async Task<IReadOnlyList<string>> GetTagsAsync(string tagsHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(tagsHref, cancellationToken);
        return ReadTags(json);
    }

    // Same advertised href as the GET — the tags resource is one address, read or replaced (ADR 0543, #416).
    public async Task<IReadOnlyList<string>> SetTagsAsync(string tagsHref, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync(tagsHref, new { tags = tags.ToArray() }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set tags ({(int)response.StatusCode}).");
        }

        return ReadTags(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task<IReadOnlyList<string>> GetTagCatalogAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("tags", cancellationToken), cancellationToken);
        return ReadTags(json);
    }

    private static IReadOnlyList<string> ReadTags(JsonElement json) =>
        json.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];

    // ---- Tag catalog admin (ADR "Tag controlled vocabulary") ----------------------------------------
    // The catalog lists LIVE tags, each advertising self (rename/recolour), retire and merge (issue #416).
    public sealed record TagCatalogItem(Guid Id, string Name, string? Color,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }
    public sealed record TagCatalog(IReadOnlyList<TagCatalogItem> Items, bool CanManage);

    public async Task<TagCatalog> GetTagCatalogWithColorsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("tags", cancellationToken), cancellationToken);
        var items = new List<TagCatalogItem>();
        if (json.TryGetProperty("catalog", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                items.Add(new TagCatalogItem(
                    e.GetProperty("id").GetGuid(),
                    e.GetProperty("name").GetString() ?? "",
                    e.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                    ParseLinks(e)));
            }
        }

        return new TagCatalog(items, json.TryGetProperty("canManage", out var cm) && cm.GetBoolean());
    }

    public async Task CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
    {
        var resp = await _http.PostAsJsonAsync(await RootHrefAsync("tags", cancellationToken), new { name, color }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await ErrorMessageAsync(resp, "Could not add the tag."));
    }

    public async Task UpdateTagAsync(TagCatalogItem tag, string? name, string? color, CancellationToken cancellationToken = default)
    {
        var resp = await _http.PutAsJsonAsync(RequireHref(tag, "self"), new { name, color }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await ErrorMessageAsync(resp, "Could not update the tag."));
    }

    public async Task RetireTagAsync(TagCatalogItem tag, CancellationToken cancellationToken = default) =>
        (await _http.DeleteAsync(RequireHref(tag, "retire"), cancellationToken)).EnsureSuccessStatusCode();

    /// <summary>Merges one tag into another, following the source row's own `merge` rel.</summary>
    public async Task MergeTagAsync(TagCatalogItem tag, Guid intoId, CancellationToken cancellationToken = default)
    {
        var resp = await _http.PostAsJsonAsync(RequireHref(tag, "merge"), new { intoId }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await ErrorMessageAsync(resp, "Could not merge the tags."));
    }

    // As ThrowIfProblemAsync: the machine code, never the server's English `detail` (issue #424).
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage resp, string fallback)
    {
        try
        {
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code) return ApiErrorText.For(code);
        }
        catch { /* not a problem+json body */ }

        return fallback;
    }

    // Bulk actions over a set of selected documents (ADR "Bulk actions on selected documents") — each POSTs
    // { ids, ... } and returns how many items succeeded vs were skipped (an item the caller can't touch or
    // that's refused is skipped, not an error).
    public sealed record BulkResult(int Succeeded, int Skipped);

    // The five operations are rels on the batch INDEX, which the root advertises as `documentsBulk` — a set of
    // ids belongs to no single resource, so there was nowhere else for them to hang (ADR 0543, issue #416).
    // Read once and cached, like the API root's own rels and the audit log's: five fixed addresses that do not
    // change between calls, so a screenful of bulk clicks does not re-read the index each time (ADR 0557).
    private async Task<string> BulkRelAsync(string rel, CancellationToken cancellationToken)
    {
        if (_bulkLinks is null)
        {
            await _bulkGate.WaitAsync(cancellationToken);
            try
            {
                _bulkLinks ??= ParseLinks(await _http.GetFromJsonAsync<JsonElement>(
                    await RootHrefAsync("documentsBulk", cancellationToken), cancellationToken))
                    ?? new Dictionary<string, string>();
            }
            finally
            {
                _bulkGate.Release();
            }
        }

        return _bulkLinks.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The bulk index advertised no '{rel}' rel (ADR 0543).");
    }

    private IReadOnlyDictionary<string, string>? _bulkLinks;
    private readonly SemaphoreSlim _bulkGate = new(1, 1);

    public async Task<BulkResult> BulkMoveAsync(IEnumerable<Guid> ids, Guid parentId, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("move", cancellationToken), new { ids = ids.ToArray(), parentId }, cancellationToken);

    public async Task<BulkResult> BulkReferenceAsync(IEnumerable<Guid> ids, Guid parentId, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("reference", cancellationToken), new { ids = ids.ToArray(), parentId }, cancellationToken);

    public async Task<BulkResult> BulkDeleteAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("delete", cancellationToken), new { ids = ids.ToArray() }, cancellationToken);

    public async Task<BulkResult> BulkAddTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("tags", cancellationToken), new { ids = ids.ToArray(), tags = tags.ToArray() }, cancellationToken);

    public async Task<BulkResult> BulkSetSensitivityAsync(IEnumerable<Guid> ids, Guid? labelId, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("sensitivity", cancellationToken), new { ids = ids.ToArray(), labelId }, cancellationToken);

    private async Task<BulkResult> PostBulkAsync(string url, object body, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(url, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"The bulk action failed ({(int)response.StatusCode}).");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new BulkResult(
            json.TryGetProperty("succeeded", out var s) ? s.GetInt32() : 0,
            json.TryGetProperty("skipped", out var k) ? k.GetInt32() : 0);
    }

    // The ONE place in this client where an id becomes a document address (ADR 0543, issue #416).
    //
    // It is the irreducible composition — turning an id back into a resource — and it is deliberately NOT
    // pretended away: every OTHER address now comes from a rel, so what remains is a single line naming a
    // single route, rather than forty call sites each knowing a different piece of the API's URL space. It
    // disappears for good when the last id-shaped view-model state becomes a row that carries its own `self`
    // (ADR 0555); until then, centralising it is what makes that final step a one-line change.
    private static string DocumentAddress(Guid documentId) => $"api/documents/{documentId}";

    // For a caller that holds an ID and no resource: FETCH the document and return the named rel. One round
    // trip, then follow — never a composed sub-resource path.
    //
    // Prefer the href overloads wherever the resource is already in hand — the detail pane holds it, so the pane
    // pays nothing.
    private async Task<string> DocumentRelAsync(Guid documentId, string rel, CancellationToken cancellationToken)
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>(DocumentAddress(documentId), cancellationToken);
        var links = ParseLinks(doc);
        return links is not null && links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"Document {documentId} advertised no '{rel}' rel (ADR 0543).");
    }

    public async Task<bool> GetSubscriptionAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        await GetSubscriptionAsync(await DocumentRelAsync(documentId, "subscription", cancellationToken), cancellationToken);

    public async Task SetSubscriptionAsync(Guid documentId, bool subscribe, CancellationToken cancellationToken = default) =>
        await SetSubscriptionAsync(await DocumentRelAsync(documentId, "subscription", cancellationToken), subscribe, cancellationToken);

    public async Task<IReadOnlyList<ReminderInfo>> GetRemindersAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        (await GetRemindersViewAsync(documentId, cancellationToken)).Reminders;

    /// <summary>
    /// The document's reminders AND the address of its target picker, from ONE read of the collection that
    /// advertises both. The Remind… dialog wants the two together; asking for them separately would mean
    /// fetching the document twice and the collection twice, which is how following rels turns into four
    /// requests where there used to be two (ADR 0543, issue #416).
    /// </summary>
    public async Task<(IReadOnlyList<ReminderInfo> Reminders, string TargetsHref)> GetRemindersViewAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var collection = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "reminders", cancellationToken), cancellationToken);
        return (ParseReminders(collection), RequireRel(collection, "targets", $"The reminders collection for {documentId}"));
    }

    public async Task CreateReminderAsync(Guid documentId, DateTimeOffset remindAt, string? note, int recurrence, Guid? targetUserId, CancellationToken cancellationToken = default) =>
        await CreateReminderAsync(await DocumentRelAsync(documentId, "reminders", cancellationToken), remindAt, note, recurrence, targetUserId, cancellationToken);

    // Takes the advertised href (detail.Href("subscription")) — one address, read/followed/unfollowed.
    public async Task<bool> GetSubscriptionAsync(string subscriptionHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(subscriptionHref, cancellationToken);
        return json.TryGetProperty("subscribed", out var s) && s.ValueKind == JsonValueKind.True;
    }

    // Follow (subscribe = true) or unfollow (false) the document.
    public async Task SetSubscriptionAsync(string subscriptionHref, bool subscribe, CancellationToken cancellationToken = default)
    {
        using var response = subscribe
            ? await _http.PutAsync(subscriptionHref, null, cancellationToken)
            : await _http.DeleteAsync(subscriptionHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not update your subscription ({(int)response.StatusCode}).");
        }
    }

    // A document reminder (Wiedervorlage, ADR "Document reminders"). Carries its own links, so cancelling one
    // follows the `cancel` rel the row advertised rather than rebuilding a path from two ids (ADR 0543/0555).
    public sealed record ReminderInfo(Guid Id, DateTimeOffset RemindAt, string? Note, int Recurrence, string RecurrenceName, string TargetName, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // Dashboard rows (ADR "My work dashboard"): a due-soon reminder / a followed document, each with the
    // document + its parent folder for click-through.
    public sealed record DashReminderInfo(Guid DocumentId, Guid? ParentId, string DocumentName, DateTimeOffset RemindAt, string? Note, int Recurrence, string RecurrenceName, bool Overdue, IReadOnlyDictionary<string, string>? Links = null);
    public sealed record DashFollowedInfo(Guid DocumentId, Guid? ParentId, string DocumentName, IReadOnlyDictionary<string, string>? Links = null);

    // The caller's overdue + due-soon reminders across all documents (the dashboard's Reminders section).
    public async Task<IReadOnlyList<DashReminderInfo>> GetDashboardRemindersAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("reminders", cancellationToken), cancellationToken);
        var list = new List<DashReminderInfo>();
        if (json.TryGetProperty("reminders", out var arr))
        {
            foreach (var r in arr.EnumerateArray())
            {
                list.Add(new DashReminderInfo(
                    r.GetProperty("documentId").GetGuid(),
                    r.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
                    r.GetProperty("documentName").GetString() ?? "",
                    r.GetProperty("remindAt").GetDateTimeOffset(),
                    r.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    r.GetProperty("recurrence").GetInt32(),
                    r.TryGetProperty("recurrenceName", out var rn) ? rn.GetString() ?? "" : "",
                    r.TryGetProperty("overdue", out var o) && o.ValueKind == JsonValueKind.True,
                    ParseLinks(r)));
            }
        }

        return list;
    }

    // The documents the caller follows (the dashboard's Following section).
    public async Task<IReadOnlyList<DashFollowedInfo>> GetDashboardFollowingAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("subscriptions", cancellationToken), cancellationToken);
        var list = new List<DashFollowedInfo>();
        if (json.TryGetProperty("followed", out var arr))
        {
            foreach (var f in arr.EnumerateArray())
            {
                list.Add(new DashFollowedInfo(
                    f.GetProperty("documentId").GetGuid(),
                    f.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
                    f.GetProperty("documentName").GetString() ?? "",
                    ParseLinks(f)));
            }
        }

        return list;
    }

    // Active tenant users the caller can target a reminder at (the picker).
    //
    // The picker belongs to the reminders COLLECTION, which is what advertises `targets` — hanging "/targets"
    // off the reminders href would be composing a URL out of one the server happened to give us, which is the
    // same mistake in nicer clothing (ADR 0543). Callers that also want the reminders should take both from
    // GetRemindersViewAsync and pass the href here, so the collection is read once rather than twice.
    public async Task<IReadOnlyList<UserOptionInfo>> GetReminderTargetsAsync(string targetsHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(targetsHref, cancellationToken);
        var list = new List<UserOptionInfo>();
        if (json.TryGetProperty("targets", out var targets))
        {
            foreach (var u in targets.EnumerateArray())
            {
                list.Add(new UserOptionInfo(u.GetProperty("id").GetGuid(), u.GetProperty("displayName").GetString() ?? ""));
            }
        }

        return list;
    }

    // The caller's pending reminders on the document (set by or targeted at them).
    // Takes the advertised href (detail.Href("reminders")).
    public async Task<IReadOnlyList<ReminderInfo>> GetRemindersAsync(string remindersHref, CancellationToken cancellationToken = default) =>
        ParseReminders(await _http.GetFromJsonAsync<JsonElement>(remindersHref, cancellationToken));

    private static List<ReminderInfo> ParseReminders(JsonElement json)
    {
        var list = new List<ReminderInfo>();
        if (json.TryGetProperty("reminders", out var reminders))
        {
            foreach (var r in reminders.EnumerateArray())
            {
                list.Add(new ReminderInfo(
                    r.GetProperty("id").GetGuid(),
                    r.GetProperty("remindAt").GetDateTimeOffset(),
                    r.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    r.GetProperty("recurrence").GetInt32(),
                    r.TryGetProperty("recurrenceName", out var rn) ? rn.GetString() ?? "" : "",
                    r.TryGetProperty("targetName", out var tn) ? tn.GetString() ?? "" : "",
                    ParseLinks(r)));
            }
        }

        return list;
    }

    // Sets a reminder; targetUserId null = the caller. Returns nothing on success, throws on a rejected request.
    public async Task CreateReminderAsync(string remindersHref, DateTimeOffset remindAt, string? note, int recurrence, Guid? targetUserId, CancellationToken cancellationToken = default)
    {
        var body = new { remindAt, note, recurrence, targetUserId };
        using var response = await _http.PostAsJsonAsync(remindersHref, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the reminder ({(int)response.StatusCode}).");
        }
    }

    /// <summary>Cancels the reminder at the address its own row advertised (ADR 0555).</summary>
    public async Task CancelReminderAsync(ReminderInfo reminder, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(RequireHref(reminder, "cancel"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not cancel the reminder ({(int)response.StatusCode}).");
        }
    }

    // Sets a version's document (issuing) date ("yyyy-MM-dd") at the address the version row advertised.
    public async Task SetDocumentDateAsync(string documentDateHref, string documentDate, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync(documentDateHref, new { documentDate }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the document date ({(int)response.StatusCode}).");
        }
    }

    // The latest confirmed version's preview + download links plus whether the preview is a converted rendition.
    public async Task<Preview> GetPreviewAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "versions", cancellationToken), cancellationToken);
        // The current version honoring the server's currentVersionId pointer (issue #265), else the latest confirmed.
        if (PickCurrentVersionElement(response) is not { } picked)
        {
            return new Preview(null, false, null, null, null, "");
        }

        var confirmed = picked.Version;

        var converted = confirmed.TryGetProperty("previewConverted", out var pc) && pc.GetBoolean();
        var extension = confirmed.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "";
        return new Preview(FindLink(confirmed, "preview"), converted, FindLink(confirmed, "download"), FindLink(confirmed, "text-layout"), FindLink(confirmed, "preview-pages"), extension, FindLink(confirmed, "annotations"));
    }

    // A preview from an ALREADY-ADVERTISED versions address, for a caller holding a row rather than an id
    // (#462). Same body as GetPreviewAsync below the first line; that one has to turn an id back into the
    // address first, which is the round trip a row-holder should not pay (ADR 0557).
    public async Task<Preview> GetPreviewFromVersionsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(versionsHref.TrimStart('/'), cancellationToken);
        if (PickCurrentVersionElement(response) is not { } picked)
        {
            return new Preview(null, false, null, null, null, "");
        }

        var confirmed = picked.Version;
        var converted = confirmed.TryGetProperty("previewConverted", out var pc) && pc.GetBoolean();
        var extension = confirmed.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "";
        return new Preview(FindLink(confirmed, "preview"), converted, FindLink(confirmed, "download"), FindLink(confirmed, "text-layout"), FindLink(confirmed, "preview-pages"), extension, FindLink(confirmed, "annotations"));
    }

    // --- Sticky notes / annotations (ADR "Document annotations") ----------------------------------------

    // The annotation list + whether the caller may create a note here (CanAnnotate, ADR "CanAnnotate right").
    public sealed record AnnotationList(IReadOnlyList<AnnotationInfo> Items, bool CanCreate);

    public async Task<AnnotationList> GetAnnotationsAsync(string annotationsUrl, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(annotationsUrl.TrimStart('/'), cancellationToken);
        var result = new List<AnnotationInfo>();
        if (json.TryGetProperty("annotations", out var arr))
        {
            foreach (var a in arr.EnumerateArray())
            {
                result.Add(new AnnotationInfo(
                    a.GetProperty("id").GetGuid(),
                    a.GetProperty("pageIndex").GetInt32(),
                    a.TryGetProperty("kind", out var k) ? k.GetInt32() : 0,
                    a.GetProperty("positionX").GetDouble(),
                    a.GetProperty("positionY").GetDouble(),
                    a.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : null,
                    a.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : null,
                    a.GetProperty("text").GetString() ?? "",
                    a.GetProperty("color").GetString() ?? "#FFEB3B",
                    a.TryGetProperty("authorName", out var an) ? an.GetString() ?? "" : "",
                    a.TryGetProperty("etag", out var et) ? et.GetString() ?? "" : "",
                    a.TryGetProperty("canEdit", out var ce) && ce.GetBoolean(),
                    a.TryGetProperty("canDelete", out var cd) && cd.GetBoolean(),
                    a.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.String ? pts.GetString() : null));
            }
        }

        return new AnnotationList(result, json.TryGetProperty("canCreate", out var cc) && cc.GetBoolean());
    }

    public async Task CreateAnnotationAsync(string annotationsUrl, int pageIndex, double x, double y, string text, string color, CancellationToken cancellationToken = default)
        => await CreateAnnotationAsync(annotationsUrl, pageIndex, 0, x, y, null, null, text, color, cancellationToken: cancellationToken);

    // Create a note (kind 0) or a markup shape (kind 1/2/3 with width/height; 4/5/6 stamp/strike/text-box; 7
    // freehand with points) — ADRs "Annotation markup" / 0525.
    public async Task CreateAnnotationAsync(string annotationsUrl, int pageIndex, int kind, double x, double y, double? width, double? height, string text, string color, string? points = null, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(annotationsUrl.TrimStart('/'), new { pageIndex, kind, positionX = x, positionY = y, width, height, text, color, points }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException("Could not add the markup.");
        }
    }

    public async Task UpdateAnnotationAsync(string annotationsUrl, Guid id, int pageIndex, double x, double y, string text, string color, string etag, CancellationToken cancellationToken = default)
        => await UpdateAnnotationAsync(annotationsUrl, id, pageIndex, x, y, null, null, text, color, etag, cancellationToken);

    public async Task UpdateAnnotationAsync(string annotationsUrl, Guid id, int pageIndex, double x, double y, double? width, double? height, string text, string color, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{annotationsUrl.TrimStart('/')}/{id}")
        {
            Content = JsonContent.Create(new { pageIndex, positionX = x, positionY = y, width, height, text, color }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException("Could not save the note.");
        }
    }

    public async Task DeleteAnnotationAsync(string annotationsUrl, Guid id, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{annotationsUrl.TrimStart('/')}/{id}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException("Could not delete the note.");
        }
    }

    // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages"); null (204) for
    // every other format, where the caller uses the single preview URL.
    public async Task<IReadOnlyList<string>?> GetPreviewPagesAsync(string previewPagesUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(previewPagesUrl.TrimStart('/'), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!json.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var urls = new List<string>();
        foreach (var page in pages.EnumerateArray())
        {
            if (page.TryGetProperty("url", out var url) && url.GetString() is { } u)
            {
                urls.Add(u);
            }
        }

        return urls.Count > 0 ? urls : null;
    }

    // Fetches the per-page word boxes for hit-overlay (ADR "Search hit overlay"). textLayoutUrl is the version
    // resource's `text-layout` link; a 204 (unsupported format / nothing recognized) yields null.
    public async Task<TextLayoutInfo?> GetTextLayoutAsync(string textLayoutUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(textLayoutUrl.TrimStart('/'), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!json.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var pageList = new List<TextLayoutPageInfo>();
        foreach (var page in pages.EnumerateArray())
        {
            var words = new List<TextLayoutBox>();
            if (page.TryGetProperty("words", out var wordArray) && wordArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in wordArray.EnumerateArray())
                {
                    words.Add(new TextLayoutBox(
                        w.GetProperty("text").GetString() ?? "",
                        w.GetProperty("x").GetDouble(),
                        w.GetProperty("y").GetDouble(),
                        w.GetProperty("width").GetDouble(),
                        w.GetProperty("height").GetDouble()));
                }
            }

            pageList.Add(new TextLayoutPageInfo(words));
        }

        return new TextLayoutInfo(pageList);
    }

    public string? GetDownloadUrl(Preview preview) => preview.DownloadUrl;

    public async Task<List<Comment>> GetCommentsAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        (await GetChatAsync(documentId, cancellationToken)).Messages;

    // The thread AND the rel that reaches its mention picker, from one request. The href has to travel with the
    // messages: it is advertised on the list resource, and re-fetching it separately would mean composing the
    // thread's URL a second time, which is exactly what ADR 0543 forbids.
    public async Task<ChatThread> GetChatAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        string? mentionableUsersHref = null;
        var messages = await LoadPagedAsync(await DocumentRelAsync(documentId, "chat", cancellationToken), "messages", ParseComment, cancellationToken,
            // First page only: the rel describes the thread, not the page.
            page => mentionableUsersHref ??= FindLink(page, "mentionable-users"));

        return new ChatThread(messages, mentionableUsersHref);
    }

    public sealed record ChatThread(List<Comment> Messages, string? MentionableUsersHref);

    // Who may be @-mentioned on this document. The server filters by who can SEE it — mentioning somebody
    // subscribes them and sends a notification carrying the document's name, so this is not a staff directory
    // (issue #383). The href comes from the thread's "mentionable-users" rel; the client never builds it.
    public async Task<IReadOnlyList<MentionableUser>> GetMentionableUsersAsync(string href, string query, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{href}?q={Uri.EscapeDataString(query)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. users.EnumerateArray().Select(u => new MentionableUser(
            u.GetProperty("id").GetGuid(),
            u.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : ""))];
    }

    public async Task PostCommentAsync(Guid documentId, string body, Guid? parentCommentId, CancellationToken cancellationToken = default)
    {
        var payload = parentCommentId is { } parent
            ? new { body, parentMessageId = parent }
            : (object)new { body };
        using var response = await _http.PostAsJsonAsync(await DocumentRelAsync(documentId, "chat", cancellationToken), payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Creates a folder = a child Document with no version (ADR 0175). Duplicate name -> 409, no permission -> 403.
    public async Task CreateFolderAsync(Guid parentId, string name, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await DocumentRelAsync(parentId, "children", cancellationToken), new { name }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A folder or document named '{name}' already exists here.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to create a folder here.");
        }

        response.EnsureSuccessStatusCode();
    }

    // ---- Tenant-admin settings (ADR "Tenant-admin settings tab") -----------------------------------

    public sealed record TenantSettingsInfo(Guid Id, string Name, string Status, DateTimeOffset CreatedAt, string DefaultOcrLanguages, int AuditRetentionDays, int CheckoutTtlDays, int CheckoutWarningDays, int WormLockMode, bool RequireMfa, bool AllowPasskeyLogin, bool RequireDispositionReview, bool RestrictTagsToCatalog, bool EnforceClearance, bool AllowExternalLinks, int ExternalLinkMaxDays, int ExternalLinkDefaultAccesses, bool ShowExternalLinkUrl, long? StorageQuotaBytes, long StorageUsedBytes, int IncompleteUploadCleanupDays, string? AuditWebhookUrl, bool AuditWebhookConfigured, int AuditWebhookConsecutiveFailures, DateTimeOffset? AuditWebhookLastSuccessAt, DateTimeOffset? AuditWebhookLastFailureAt, DateTimeOffset? AuditWebhookNextAttemptAt, string? AuditWebhookLastError,
        IReadOnlyDictionary<string, string>? Links = null);

    private static DateTimeOffset? OptDate(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;

    private static TenantSettingsInfo ParseTenantSettings(JsonElement j) => new(
        j.GetProperty("id").GetGuid(),
        j.GetProperty("name").GetString() ?? "",
        j.GetProperty("status").GetString() ?? "",
        j.GetProperty("createdAt").GetDateTimeOffset(),
        j.GetProperty("defaultOcrLanguages").GetString() ?? "",
        j.TryGetProperty("auditRetentionDays", out var r) ? r.GetInt32() : 0,
        j.TryGetProperty("checkoutTtlDays", out var c) ? c.GetInt32() : 0,
        j.TryGetProperty("checkoutWarningDays", out var cw) ? cw.GetInt32() : 1,
        j.TryGetProperty("wormLockMode", out var w) ? w.GetInt32() : 0,
        j.TryGetProperty("requireMfa", out var m) && m.ValueKind == JsonValueKind.True,
        j.TryGetProperty("allowPasskeyLogin", out var pk) && pk.ValueKind == JsonValueKind.True,
        j.TryGetProperty("requireDispositionReview", out var dr) && dr.ValueKind == JsonValueKind.True,
        j.TryGetProperty("restrictTagsToCatalog", out var rt) && rt.ValueKind == JsonValueKind.True,
        j.TryGetProperty("enforceClearance", out var ec) && ec.ValueKind == JsonValueKind.True,
        j.TryGetProperty("allowExternalLinks", out var xl) && xl.ValueKind == JsonValueKind.True,
        j.TryGetProperty("externalLinkMaxDays", out var xd) ? xd.GetInt32() : 180,
        j.TryGetProperty("externalLinkDefaultAccesses", out var xa) ? xa.GetInt32() : 5,
        j.TryGetProperty("showExternalLinkUrl", out var xu) && xu.GetBoolean(),
        j.TryGetProperty("storageQuotaBytes", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt64() : null,
        j.TryGetProperty("storageUsedBytes", out var su) && su.ValueKind == JsonValueKind.Number ? su.GetInt64() : 0,
        j.TryGetProperty("incompleteUploadCleanupDays", out var iu) ? iu.GetInt32() : 0,
        j.TryGetProperty("auditWebhookUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
        j.TryGetProperty("auditWebhookConfigured", out var cf) && cf.ValueKind == JsonValueKind.True,
        j.TryGetProperty("auditWebhookConsecutiveFailures", out var f) ? f.GetInt32() : 0,
        OptDate(j, "auditWebhookLastSuccessAt"),
        OptDate(j, "auditWebhookLastFailureAt"),
        OptDate(j, "auditWebhookNextAttemptAt"),
        j.TryGetProperty("auditWebhookLastError", out var le) && le.ValueKind == JsonValueKind.String ? le.GetString() : null,
        ParseLinks(j));

    public async Task<TenantSettingsInfo> GetTenantSettingsAsync(CancellationToken cancellationToken = default)
    {
        var j = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("tenantSettings", cancellationToken), cancellationToken);
        return ParseTenantSettings(j);
    }

    // The tenant-settings resource's own maintenance actions (issue #416). Both are rels ON that resource, so
    // reaching them means reading it first — paid once per admin click, which is the trade the root's
    // "collection roots only" rule asks for: an action on a resource is advertised by that resource, not by the
    // root. (Contrast the notification badge, which is polled and therefore earned a root rel of its own.)
    private async Task<string> TenantSettingsRelAsync(string rel, CancellationToken cancellationToken)
    {
        var settings = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("tenantSettings", cancellationToken), cancellationToken);
        return ParseLinks(settings) is { } links && links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"Tenant settings advertised no '{rel}' rel (ADR 0543).");
    }

    // Sends a synthetic test event to the tenant's saved SIEM webhook (ADR "Audit webhook test delivery") — returns
    // whether the endpoint accepted it + the error on failure.
    public async Task<(bool Success, string? Error)> TestAuditWebhookAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await TenantSettingsRelAsync("audit-webhook-test", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("Save the webhook URL + secret before sending a test.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to test the audit webhook.");
        }

        response.EnsureSuccessStatusCode();
        var j = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (j.GetProperty("success").GetBoolean(),
            j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null);
    }

    // Rebuilds the tenant's used-storage counter from the actual stored blobs (ADR "Per-tenant storage quota").
    public async Task<TenantSettingsInfo> RecomputeStorageAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await TenantSettingsRelAsync("recompute-storage", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to recompute storage usage.");
        }

        response.EnsureSuccessStatusCode();
        var j = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseTenantSettings(j);
    }

    // NOTE: this PUT is a FULL REPLACEMENT — a field left out of the payload is set to its DTO default, not left
    // alone. The external-link settings are therefore REQUIRED parameters rather than optional ones: when they
    // were simply missing here, a desktop admin saving any unrelated tenant setting silently switched external
    // links off AND set both caps to 0. An optional default would recreate exactly that bug at the next caller.
    // ONE generic per-group save (#530 tranche 10, ADR "Per-group tenant settings"): the caller passes the
    // already-read settings (whose links carry the writable sub-resources) plus the group's rel suffix and its
    // payload. Follows the advertised settings-<group> rel (ADR 0543) — a missing rel means "not offered".
    public async Task<TenantSettingsInfo> SaveTenantSettingsGroupAsync(TenantSettingsInfo settings, string group, object body, CancellationToken cancellationToken = default)
    {
        var href = settings.Links?.GetValueOrDefault($"settings-{group}")
            ?? throw new ApiActionException("The server offered no way to edit these settings.");
        using var response = await _http.PutAsJsonAsync(href, body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("Another active tenant already uses this name.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("Check the entered values (name, OCR languages, retention, webhook URL/secret).");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage tenant settings.");
        }

        response.EnsureSuccessStatusCode();
        var j = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseTenantSettings(j);
    }

    public async Task CreateRepositoryAsync(string name, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await RootHrefAsync("repositories", cancellationToken), new { name }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A repository named '{name}' already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to create repositories.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Renames a document/folder. Both this and DeleteAsync require an If-Match ETag (ADR 0188), fetched via
    // a HEAD first. 409 = duplicate sibling name, 403 = no permission (CanEditIndexData), 412 = changed since
    // it was loaded.
    public async Task RenameAsync(Guid documentId, string newName, CancellationToken cancellationToken = default)
    {
        var etag = await GetETagAsync(documentId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, DocumentAddress(documentId))
        {
            Content = JsonContent.Create(new { name = newName }),
        };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A folder or document named '{newName}' already exists here.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to rename this item.");
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ApiActionException("This item changed since you loaded it — refresh and try again.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Soft-deletes a document/folder to the recycle bin (a folder cascades to its whole subtree, ADR 0196).
    // Requires If-Match (ADR 0188). 403 = no permission (CanDelete), 412 = changed since it was loaded.
    public async Task DeleteAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var etag = await GetETagAsync(documentId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Delete, DocumentAddress(documentId));
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to delete this item.");
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ApiActionException("This item changed since you loaded it — refresh and try again.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Restores a soft-deleted document/folder (and its cascade-deleted descendants). Idempotent, no If-Match
    // (ADR 0196). 403 = no permission (CanDelete).
    public async Task RestoreAsync(IAdvertisesLinks entry, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(entry, "restore"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to restore this item.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Every deleted Document at any depth under a repository root (ADR 0196).
    // Follows the repository ROW's own `recycle-bin` rel (issue #416) — a repository is a document, and its bin
    // is one of the addresses the listing hands over.
    public Task<List<RecycleBinItem>> GetRecycleBinAsync(Node repository, CancellationToken cancellationToken = default) =>
        LoadPagedAsync(
            repository.Href("recycle-bin") ?? throw new InvalidOperationException($"The repository '{repository.Name}' advertised no 'recycle-bin' rel (ADR 0543/0555)."),
            "items", ParseRecycleBinItem, cancellationToken);

    // Permanently purges a recycle-bin item + its subtree (ADR "Manual hard-delete / purge") — tenant-admin only.
    public async Task PurgeAsync(IAdvertisesLinks entry, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(entry, "purge"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can permanently purge items.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("This item is under a legal hold and cannot be purged.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Empties a repository's recycle bin — permanently purges every item in it (ADR "Manual hard-delete / purge").
    public async Task EmptyRecycleBinAsync(Node repository, CancellationToken cancellationToken = default)
    {
        var bin = await _http.GetFromJsonAsync<JsonElement>(
            repository.Href("recycle-bin") ?? throw new InvalidOperationException($"The repository '{repository.Name}' advertised no 'recycle-bin' rel (ADR 0543/0555)."),
            cancellationToken);
        using var response = await _http.PostAsync(RequireRel(bin, "purge-all", $"The recycle bin of '{repository.Name}'"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can empty the recycle bin.");
        }

        response.EnsureSuccessStatusCode();
    }

    // One soft-deleted document in the tenant-wide recycle bin (ADR "Recycle bin tab" / "Desktop recycle bin
    // parity"): its name, full path, when it was deleted, and by whom (from the audit trail).
    // A soft-deleted document. Its own `restore`/`purge` addresses come from the ROW, because the document is
    // behind the soft-delete query filter — there is no resource left to fetch them from (ADR 0543/0555).
    public sealed record RecycleBinEntry(Guid Id, string Name, string Path, DateTimeOffset DeletedAt, string DeletedBy,
        IReadOnlyDictionary<string, string>? Links = null) : IAdvertisesLinks
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // The bin plus what can be done to it as a whole — captured where the collection is read, so the tab does
    // not pay a request per button (ADR 0557).
    public sealed record RecycleBinList(IReadOnlyList<RecycleBinEntry> Items, IReadOnlyDictionary<string, string> Links);

    // Every soft-deleted document the caller can see, tenant-wide (ADR "Recycle bin tab") — capped at 500 by the
    // Api (Truncated flag ignored here; the tab tells the user if more exist via the status line).
    public async Task<RecycleBinList> GetRecycleBinItemsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("recycleBin", cancellationToken), cancellationToken);
        var items = new List<RecycleBinEntry>();
        if (response.TryGetProperty("items", out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                items.Add(new RecycleBinEntry(
                    item.GetProperty("id").GetGuid(),
                    item.GetProperty("name").GetString() ?? "",
                    item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    item.GetProperty("deletedAt").GetDateTimeOffset(),
                    item.TryGetProperty("deletedBy", out var db) ? db.GetString() ?? "—" : "—",
                    ParseLinks(item)));
            }
        }

        return new RecycleBinList(items, ParseLinks(response) ?? new Dictionary<string, string>());
    }

    // Empties the whole tenant-wide recycle bin — permanently purges every soft-deleted document (ADR "Recycle
    // bin tab") — tenant-admin only.
    public async Task PurgeRecycleBinAsync(string purgeAllHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(purgeAllHref, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can empty the recycle bin.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Bulk restore (ADR "Bulk restore from the recycle bin") — restores each requested soft-deleted document +
    // its subtree in one call; returns how many were restored vs skipped (already active / gone / not permitted).
    public async Task<(int Restored, int Skipped)> RestoreManyAsync(string restoreSelectedHref, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(restoreSelectedHref, new { ids }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (json.GetProperty("restored").GetInt32(), json.GetProperty("skipped").GetInt32());
    }

    // Bulk purge of selected items (ADR "Bulk purge of selected recycle-bin items") — tenant-admin; permanently
    // removes each requested recycle-bin root + subtree; returns purged vs skipped (gone / active / held / WORM).
    public async Task<(int Purged, int Skipped)> PurgeManyAsync(string purgeSelectedHref, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(purgeSelectedHref, new { ids }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can purge items.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (json.GetProperty("purged").GetInt32(), json.GetProperty("skipped").GetInt32());
    }

    // The references (shortcuts) filed in a folder — see ADR "Desktop drag-and-drop move and reference".
    public async Task<List<Reference>> GetReferencesAsync(Guid folderId, CancellationToken cancellationToken = default) =>
        await LoadPagedAsync(await DocumentRelAsync(folderId, "references", cancellationToken), "references", ParseReference, cancellationToken);

    public Task<List<Reference>> GetReferencesAsync(string referencesHref, CancellationToken cancellationToken = default) =>
        LoadPagedAsync(referencesHref, "references", ParseReference, cancellationToken);

    // The folders that reference a given item (with full paths) — see ADR "References-of-an-item list".
    public async Task<List<ReferencingFolder>> GetReferencingFoldersAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        await LoadPagedAsync(await DocumentRelAsync(documentId, "referencing-folders", cancellationToken), "folders", ParseReferencingFolder, cancellationToken);

    // The full references view — the item's real primary location plus every referencing folder (ADR 0506). The
    // primary location is a top-level object on the first page (not part of the paged array), so this can't reuse
    // LoadPagedAsync; it walks the pages itself.
    public async Task<ReferencesView> GetReferencesViewAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var folders = new List<ReferencingFolder>();
        ReferencingFolder? primary = null;
        string? next = await DocumentRelAsync(documentId, "referencing-folders", cancellationToken);
        var first = true;

        while (next is not null)
        {
            var page = await _http.GetFromJsonAsync<JsonElement>(next, cancellationToken);
            if (first)
            {
                if (page.TryGetProperty("primaryLocation", out var pl) && pl.ValueKind == JsonValueKind.Object)
                {
                    primary = ParseReferencingFolder(pl);
                }

                first = false;
            }

            if (page.TryGetProperty("folders", out var array))
            {
                folders.AddRange(array.EnumerateArray().Select(ParseReferencingFolder));
            }

            next = FindLink(page, "next");
        }

        return new ReferencesView(primary, folders);
    }

    // Promotes a referenced folder to be the document's primary location (ADR 0506): atomic move + leave a
    // reference at the former home. Same If-Match contract as MoveAsync.
    public async Task SetPrimaryLocationAsync(Guid documentId, Guid folderId, CancellationToken cancellationToken = default)
    {
        var etag = await GetETagAsync(documentId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, await DocumentRelAsync(documentId, "set-primary-location", cancellationToken))
        {
            Content = JsonContent.Create(new { folderId }),
        };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            throw new CannotSetPrimaryLocationException();
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SetPrimaryLocationForbiddenException();
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new PrimaryLocationConcurrencyException();
        }

        response.EnsureSuccessStatusCode();
    }

    // Free-text metadata search across the tenant (names + index-field values) — see ADR "Metadata search
    // (first slice)". Follows the next links to load all pages.
    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        await LoadPagedAsync($"{await RootHrefAsync("search", cancellationToken)}?q={Uri.EscapeDataString(query)}", "results", ParseSearchResult, cancellationToken);

    // Runs a search from a pre-assembled query string (q + repositoryId + system[..]/fields[..] filters) —
    // see ADR "Search-refinement UI".
    public async Task<List<SearchResult>> SearchWithFiltersAsync(string queryString, CancellationToken cancellationToken = default) =>
        await LoadPagedAsync($"{await RootHrefAsync("search", cancellationToken)}?{queryString}", "results", ParseSearchResult, cancellationToken);

    // Search facets (ADR "Search facets") — document type / created-by / year counts to drill down by.
    public sealed record SearchFacetBucket(string Value, long Count);
    public sealed record SearchFieldFacet(string Name, IReadOnlyList<SearchFacetBucket> Buckets);
    public sealed record SearchFacets(IReadOnlyList<SearchFacetBucket> DocumentTypes, IReadOnlyList<SearchFacetBucket> CreatedBy, IReadOnlyList<SearchFacetBucket> Years, IReadOnlyList<SearchFacetBucket> Tags, IReadOnlyList<SearchFacetBucket> FileTypes, IReadOnlyList<SearchFacetBucket> SensitivityLabels, IReadOnlyList<SearchFieldFacet> Fields);
    public sealed record SearchResults(IReadOnlyList<SearchResult> Results, SearchFacets Facets);

    // Like SearchWithFiltersAsync but also returns the facet counts (from the first page — they're the same
    // across pages), for the refinement panel.
    public async Task<SearchResults> SearchWithFacetsAsync(string queryString, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        var facets = new SearchFacets([], [], [], [], [], [], []);
        string? next = $"{await RootHrefAsync("search", cancellationToken)}?{queryString}";
        var first = true;
        while (next is not null)
        {
            var page = await _http.GetFromJsonAsync<JsonElement>(next, cancellationToken);
            if (page.TryGetProperty("results", out var array))
            {
                results.AddRange(array.EnumerateArray().Select(ParseSearchResult));
            }

            if (first)
            {
                facets = ParseFacets(page);
                first = false;
            }

            next = FindLink(page, "next");
        }

        return new SearchResults(results, facets);
    }

    private static SearchFacets ParseFacets(JsonElement page)
    {
        if (!page.TryGetProperty("facets", out var f) || f.ValueKind != JsonValueKind.Object)
        {
            return new SearchFacets([], [], [], [], [], [], []);
        }

        static IReadOnlyList<SearchFacetBucket> BucketsOf(JsonElement arr)
        {
            var list = new List<SearchFacetBucket>();
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in arr.EnumerateArray())
                {
                    list.Add(new SearchFacetBucket(b.GetProperty("value").GetString() ?? "", b.GetProperty("count").GetInt64()));
                }
            }

            return list;
        }

        static IReadOnlyList<SearchFacetBucket> Buckets(JsonElement facets, string group) =>
            facets.TryGetProperty(group, out var arr) ? BucketsOf(arr) : [];

        var fields = new List<SearchFieldFacet>();
        if (f.TryGetProperty("fields", out var fieldArr) && fieldArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ff in fieldArr.EnumerateArray())
            {
                fields.Add(new SearchFieldFacet(
                    ff.GetProperty("name").GetString() ?? "",
                    ff.TryGetProperty("buckets", out var b) ? BucketsOf(b) : []));
            }
        }

        return new SearchFacets(Buckets(f, "documentTypes"), Buckets(f, "createdBy"), Buckets(f, "years"), Buckets(f, "tags"), Buckets(f, "fileTypes"), Buckets(f, "sensitivityLabels"), fields);
    }

    // ---- Saved searches (ADR "Saved searches") ------------------------------------------------------

    // ShareScope: 0 = Private, 1 = Everyone, 2 = Specific (ADR "Scoped saved-search sharing").
    // Only the OWNER's rows advertise self/delete/shares, so a search shared with you carries none of them.
    public sealed record SavedSearchInfo(Guid Id, string Name, string QueryString, int ShareScope, bool IsMine, string OwnerName,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;

        public bool IsEveryone => ShareScope == 1;
        public bool IsSpecific => ShareScope == 2;
    }

    public sealed record ShareTargetInfo(string Type, Guid Id, string Name);
    public sealed record ShareGrantInfo(string PrincipalType, Guid PrincipalId);

    public async Task<List<SavedSearchInfo>> GetSavedSearchesAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("savedSearches", cancellationToken), cancellationToken);
        var list = new List<SavedSearchInfo>();
        if (json.TryGetProperty("savedSearches", out var arr))
        {
            foreach (var s in arr.EnumerateArray())
            {
                list.Add(new SavedSearchInfo(
                    s.GetProperty("id").GetGuid(),
                    s.GetProperty("name").GetString() ?? "",
                    s.GetProperty("queryString").GetString() ?? "",
                    s.TryGetProperty("shareScope", out var sc) ? sc.GetInt32() : 0,
                    !s.TryGetProperty("isMine", out var mine) || mine.ValueKind != JsonValueKind.False,
                    s.TryGetProperty("ownerName", out var on) ? on.GetString() ?? "" : "",
                    ParseLinks(s)));
            }
        }

        return list;
    }

    // The picker options (active users + groups) for the share dialog.
    public async Task<List<ShareTargetInfo>> GetShareTargetsAsync(CancellationToken cancellationToken = default)
    {
        // `share-targets` is advertised by the saved-searches collection — the dialog that needs it opens from
        // that list, so the read is one the screen has effectively already paid for.
        var collection = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("savedSearches", cancellationToken), cancellationToken);
        var targetsHref = ParseLinks(collection) is { } collectionLinks && collectionLinks.TryGetValue("share-targets", out var t)
            ? t
            : throw new InvalidOperationException("Saved searches advertised no 'share-targets' rel (ADR 0543).");

        var json = await _http.GetFromJsonAsync<JsonElement>(targetsHref, cancellationToken);
        var list = new List<ShareTargetInfo>();
        if (json.TryGetProperty("users", out var users))
        {
            foreach (var u in users.EnumerateArray())
            {
                list.Add(new ShareTargetInfo("user", u.GetProperty("id").GetGuid(), u.GetProperty("displayName").GetString() ?? ""));
            }
        }

        if (json.TryGetProperty("groups", out var groups))
        {
            foreach (var g in groups.EnumerateArray())
            {
                list.Add(new ShareTargetInfo("group", g.GetProperty("id").GetGuid(), g.GetProperty("name").GetString() ?? ""));
            }
        }

        return list;
    }

    // The current specific-principal grants on my search (owner-only).
    public async Task<List<ShareGrantInfo>> GetSavedSearchSharesAsync(SavedSearchInfo search, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(RequireHref(search, "shares"), cancellationToken);
        var list = new List<ShareGrantInfo>();
        if (json.TryGetProperty("shares", out var arr))
        {
            foreach (var g in arr.EnumerateArray())
            {
                list.Add(new ShareGrantInfo(g.GetProperty("principalType").GetString() ?? "", g.GetProperty("principalId").GetGuid()));
            }
        }

        return list;
    }

    public async Task SaveSearchAsync(string name, string queryString, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await RootHrefAsync("savedSearches", cancellationToken), new { name, queryString }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("You already have a saved search with that name.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Set the scope + specific-principal grants on my own saved search (ADR "Scoped saved-search sharing") —
    // owner-only PUT. shares carries the ("user"|"group", id) principals (only applied when scope == Specific).
    public async Task SetSavedSearchShareAsync(SavedSearchInfo search, int shareScope, IReadOnlyList<(string Type, Guid Id)> shares, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(
            RequireHref(search, "self"),
            new { name = search.Name, queryString = search.QueryString, shareScope, shares = shares.Select(s => new { type = s.Type, id = s.Id }) },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteSavedSearchAsync(SavedSearchInfo search, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(RequireHref(search, "delete"), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // The tenant's distinct index-field names + types, for the refinement UI's field picker.
    public sealed record SearchField(string Name, int DataType);

    public async Task<IReadOnlyList<SearchField>> GetSearchFieldsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("searchFields", cancellationToken), cancellationToken);
        var fields = new List<SearchField>();
        if (json.TryGetProperty("fields", out var array))
        {
            foreach (var f in array.EnumerateArray())
            {
                fields.Add(new SearchField(
                    f.GetProperty("name").GetString() ?? "",
                    f.TryGetProperty("dataType", out var dataType) ? dataType.GetInt32() : 0));
            }
        }

        return fields;
    }

    // Moves (reparents) an item into another folder. Requires If-Match (like rename/delete), fetched via a
    // HEAD. 400 = into its own subtree, 403 = no permission (CanMove/CanCreateSubItems), 409 = name clash.
    public async Task MoveAsync(Guid documentId, Guid newParentId, CancellationToken cancellationToken = default)
    {
        var etag = await GetETagAsync(documentId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, await DocumentRelAsync(documentId, "move", cancellationToken))
        {
            Content = JsonContent.Create(new { parentId = newParentId }),
        };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("Can't move an item into itself or one of its own sub-folders.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to move this item here.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("An item with that name already exists in the target folder.");
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ApiActionException("This item changed since you loaded it — refresh and try again.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Files a reference (shortcut) to an item into a folder. 400 = into its own subtree, 403 = no permission,
    // 409 = already referenced here.
    // Duplicate detection (ADR "Duplicate document detection") — documents whose latest confirmed version is
    // byte-identical to the given SHA-256, ACL-filtered. Used to warn before an upload.
    public sealed record DuplicateInfo(Guid Id, string Name, string Path);

    public async Task<List<DuplicateInfo>> FindDuplicatesAsync(string hash, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(
            $"{await RootHrefAsync("duplicates", cancellationToken)}?hash={hash}", cancellationToken);
        var list = new List<DuplicateInfo>();
        if (json.TryGetProperty("duplicates", out var arr))
        {
            foreach (var d in arr.EnumerateArray())
            {
                list.Add(new DuplicateInfo(
                    d.GetProperty("id").GetGuid(),
                    d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    d.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ""));
            }
        }

        return list;
    }

    public async Task CreateReferenceAsync(Guid folderId, Guid targetId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await DocumentRelAsync(folderId, "references", cancellationToken), new { targetId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("Can't reference an item into itself or one of its own sub-folders.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to place a reference here.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("This item is already referenced in that folder.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Removes a reference (the shortcut only, never the target) at the address its own row advertised.
    public async Task DeleteReferenceAsync(string deleteHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(deleteHref, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to remove this reference.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Reads the current ETag (a HEAD, cheaper than GET) so a rename/delete can send it as If-Match.
    private async Task<EntityTagHeaderValue?> GetETagAsync(Guid documentId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, DocumentAddress(documentId));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag;
    }

    // Uploads a file into a folder, mirroring the web client's drag-drop flow (ADR 0216): create the child
    // Document, create a Pending version, PUT the bytes straight to the presigned URL (never proxied), then
    // finalise (server hashes + assigns the version number). The server assigns the mask at finalize (eMail
    // for .eml/.msg, else Basic Entry — ADR "Email auto-classification"), so the client doesn't classify.
    // Returns the created document's id. An optional feed comment is posted on it after finalize (ADR "Filing
    // posts a feed comment") — used by list-pane drop filing into a folder (ADR "List-pane drop filing").
    public async Task<Guid> UploadFileAsync(Guid folderId, string fileName, byte[] bytes, string? comment = null, CancellationToken cancellationToken = default) =>
        await UploadFileAsync(await DocumentRelAsync(folderId, "children", cancellationToken), fileName, bytes, comment, cancellationToken);

    /// <summary>The same upload, posted to a children address the caller already holds.</summary>
    /// <remarks>
    /// The href overload is the real one. A drop of several files into one folder resolves that folder's
    /// <c>children</c> rel ONCE and files every file through it, rather than fetching the folder again per file —
    /// following a rel must not cost a request per use (ADR 0557). The id overload above is what the view model,
    /// whose "where am I" state is still a <see cref="Guid"/>, calls.
    /// </remarks>
    public async Task<Guid> UploadFileAsync(string childrenHref, string fileName, byte[] bytes, string? comment = null, CancellationToken cancellationToken = default)
    {
        // Document.Name is the stem (no extension); the extension rides on the version's object key (ADR
        // "Extension off Document.Name, derived from the object key").
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        using var createResponse = await _http.PostAsJsonAsync(childrenHref, new { name }, cancellationToken);
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DocumentNameTakenException(fileName);
        }

        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException($"'{fileName}': you don't have permission to upload here.");
        }

        createResponse.EnsureSuccessStatusCode();
        // The create response IS the new document — id AND the address of its versions collection. Reading only
        // the id here is what used to force the next two steps to rebuild paths from it (ADR 0543, issue #416).
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var documentId = created.GetProperty("id").GetGuid();

        // The filing comment is the first version's "why this revision" note (ADR 0528) — set on the version,
        // not posted to the chat feed as it used to be.
        var versionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        using var versionResponse = await _http.PostAsJsonAsync(RequireRel(created, "versions", "The created document"), new { fileExtension = extension, comment = versionComment }, cancellationToken);
        versionResponse.EnsureSuccessStatusCode();
        var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var uploadUrl = version.GetProperty("uploadUrl").GetString()!;

        using var uploadContent = new ByteArrayContent(bytes);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(fileName));
        using var uploadResponse = await Anonymous.PutAsync(uploadUrl, uploadContent, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();

        // Finalize is a PUT to the version's OWN address, which the create response just advertised as `self`.
        using var finalizeResponse = await _http.PutAsync(RequireRel(version, "self", "The pending version"), null, cancellationToken);
        finalizeResponse.EnsureSuccessStatusCode();

        // The server assigns the mask at finalize (eMail for .eml/.msg, else Basic Entry) — ADR "Email
        // auto-classification"; the client no longer classifies.

        return documentId;
    }

    /// <summary>What is already in the target folder under a dropped file's name, and a free name to offer instead.</summary>
    /// <param name="existing">The row whose name collided, or null if it went away between the 409 and this read.
    /// It carries its own addresses, so filing a new version of it follows the row's <c>versions</c> rel.</param>
    /// <param name="suggestedName">A stem not currently taken here — "Invoice" becomes "Invoice (2)".</param>
    public sealed record NameConflict(Node? Existing, string SuggestedName);

    /// <summary>
    /// Reads the target folder ONCE and answers both questions a name conflict raises.
    /// </summary>
    /// <remarks>
    /// One listing rather than "does it exist?" plus "what name is free?": the same rows answer both, and the
    /// rows carry the addresses the resolution then follows (ADRs 0555/0557). The suggested name is a starting
    /// point only — the user may type anything, and the server has the final say on uniqueness.
    /// </remarks>
    public async Task<NameConflict> DescribeNameConflictAsync(string childrenHref, string stem, CancellationToken cancellationToken = default)
    {
        var (children, _) = await GetFolderContentsAsync(childrenHref, cancellationToken);
        var existing = children.FirstOrDefault(c => string.Equals(c.Name, stem, StringComparison.OrdinalIgnoreCase));
        var taken = children.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var n = 2; n < 1000; n++)
        {
            if (!taken.Contains($"{stem} ({n})"))
            {
                return new NameConflict(existing, $"{stem} ({n})");
            }
        }

        return new NameConflict(existing, $"{stem} ({Guid.NewGuid().ToString("N")[..6]})");
    }

    /// <summary>An inline preview of a check-out's WORKING COPY — what you are about to check in.</summary>
    /// <remarks>
    /// Follows the row's own `preview` rel (ADRs 0543/0555). The rel is absent until a working copy has been
    /// saved, and its absence means exactly that — there is nothing to preview — so it is not an error.
    /// </remarks>
    public async Task<Preview?> GetCheckoutPreviewAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        if (checkout.Href("preview") is not { } href)
        {
            return null;
        }

        using var response = await _http.GetAsync(href, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // No text layout / pages / annotations: those belong to an archived VERSION, and a working copy is not
        // one yet. The preview is the picture, nothing more.
        return new Preview(
            json.GetProperty("previewUrl").GetString(),
            json.TryGetProperty("previewConverted", out var c) && c.ValueKind == JsonValueKind.True,
            null, null, null, checkout.FileExtension);
    }

    // ---- Check-out / check-in (ADR "Document check-out / check-in") -----------------------------------

    // A held check-out, carrying the addresses its own row advertised (ADR 0543/0555): `checkin`,
    // `working-copy`, `extend` and — only when there is a stash to diff — `compare`.
    // ImplicitAgent: the client that took this lock without the user asking — a save-by-rename edit over the
    // WebDAV mount (ADR 0562); null for an explicit check-out. Client-supplied text: display it, never act on it.
    public sealed record CheckoutItem(Guid Id, string Name, string Path, string Sha256, string FileExtension, bool HasStash, bool IsModified, string? StashDownloadUrl, DateTimeOffset? ExpiresAt, IReadOnlyDictionary<string, string>? Links = null, string? ImplicitAgent = null, bool? IsSigned = null, string? DownloadUrl = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // Acquire the exclusive edit lock. 409 = already held by someone else; 403 = no permission / not a User.
    public async Task CheckOutAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsync(await DocumentRelAsync(documentId, "checkout", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("This document is already checked out by another user.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to check out this document.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Release the lock — used for check-in / unlock / discard (the holder) and override (a CanOverrideCheckout
    // holder force-releasing someone else's). Idempotent when not checked out.
    public async Task CheckInAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(await DocumentRelAsync(documentId, "cancel-checkout", cancellationToken), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to release this check-out.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Stash-based check-in (ADR 0513): the server promotes the cloud stash (the WebDAV-edited working copy) to a new
    // confirmed version and releases the lock — the desktop no longer uploads a local file. Holder-only; 400 if
    // there's no stash to check in (nothing changed).
    public async Task CheckInFromStashAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(checkout, "checkin"), new { }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to check in this document.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("There are no changes to check in.");
        }

        response.EnsureSuccessStatusCode();
    }

    // "Extend my check-out" (ADR "Self-service check-out extension") — resets the auto-release idle timer. The
    // holder or a CanOverrideCheckout admin; 409 if the document isn't checked out.
    public async Task ExtendCheckoutAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(checkout, "extend"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to extend this check-out.");
        }

        response.EnsureSuccessStatusCode();
    }

    // The caller's currently checked-out documents (tenant-wide), each with the current version's SHA-256.
    public async Task<List<CheckoutItem>> GetCheckoutsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("checkouts", cancellationToken), cancellationToken);
        var items = new List<CheckoutItem>();
        if (json.TryGetProperty("items", out var arr))
        {
            foreach (var i in arr.EnumerateArray())
            {
                items.Add(new CheckoutItem(
                    i.GetProperty("id").GetGuid(), i.GetProperty("name").GetString() ?? "",
                    i.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    i.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "",
                    i.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "",
                    i.TryGetProperty("hasStash", out var hst) && hst.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("isModified", out var im) && im.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("stashDownloadUrl", out var sdu) && sdu.ValueKind == JsonValueKind.String ? sdu.GetString() : null,
                    i.TryGetProperty("expiresAt", out var ea) && ea.ValueKind == JsonValueKind.String ? ea.GetDateTimeOffset() : null,
                    ParseLinks(i),
                    i.TryGetProperty("implicitAgent", out var ia) && ia.ValueKind == JsonValueKind.String ? ia.GetString() : null,
                    // Tri-state: absent means never examined (#491), which is not the same as "not signed".
                    i.TryGetProperty("isSigned", out var sg) && sg.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? sg.GetBoolean()
                        : null,
                    StrOrNull(i, "downloadUrl")));
            }
        }

        return items;
    }

    // "Save to cloud" — uploads the in-progress working copy to the S3 stash so it survives logout/close and is
    // re-downloaded on next login (ADR "Check-out working-copy stash + exit guard"). Holder-only server-side.
    public async Task SaveWorkingCopyAsync(CheckoutItem checkout, byte[] bytes, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(checkout, "working-copy"), new { }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't hold the check-out on this document.");
        }

        response.EnsureSuccessStatusCode();
        var uploadUrl = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("uploadUrl").GetString()!;

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var upload = await Anonymous.PutAsync(uploadUrl, content, cancellationToken);
        upload.EnsureSuccessStatusCode();
    }

    // Downloads the cloud working-copy stash bytes (restoring in-progress edits on login).
    public async Task<byte[]> DownloadStashAsync(string stashDownloadUrl, CancellationToken cancellationToken = default)
    {
        var (bytes, _) = await DownloadAsync(stashDownloadUrl, cancellationToken);
        return bytes;
    }

    // Downloads the current confirmed version's bytes (for writing to the local checkout working copy).
    public async Task<byte[]> DownloadCurrentVersionAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var preview = await GetPreviewAsync(documentId, cancellationToken);
        if (preview.DownloadUrl is null)
        {
            throw new ApiActionException("This document has no downloadable version.");
        }

        var (bytes, _) = await DownloadAsync(preview.DownloadUrl, cancellationToken);
        return bytes;
    }

    // ---- Version comparison (ADR "Document version comparison") ----
    // A version row, carrying the links its own row advertised — `restore` and `document-date` are followed
    // from here rather than rebuilt from a document id and a version id (ADR 0543/0555).
    public sealed record VersionInfo(Guid Id, int? VersionNumber, string Status, string FileExtension, string? DownloadUrl,
        string DocumentDate = "", DateTimeOffset CreatedAt = default, string CreatedByName = "", bool IsCurrent = false,
        string? Comment = null, IReadOnlyDictionary<string, string>? Links = null, string? WorkflowStatus = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }
    public sealed record DiffLineInfo(int Op, string Text);
    public sealed record VersionComparison(bool Available, List<DiffLineInfo> Lines);

    // Restores (rolls back to) an earlier version (ADR "Version restore") — creates a new current version from
    // its content. Throws on a rejected request (403 no edit rights, 409 workflow/hold/checkout).
    public async Task RestoreVersionAsync(VersionInfo version, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(version, "restore"), null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode == HttpStatusCode.Conflict
                ? "the document is under a workflow, legal hold, or checked out"
                : $"HTTP {(int)response.StatusCode}";
            throw new ApiActionException($"Could not restore this version ({reason}).");
        }
    }

    // The confirmed versions of a document (newest first), each with its presigned download URL.
    // Takes the advertised href (node.Href("versions")), not a document id (ADR 0543, issue #416).
    /// <summary>
    /// The version list plus the collection's own `compare` address — one read, so a screen that offers
    /// comparison does not pay a second request to learn where to send it (issue #416).
    /// </summary>
    public async Task<(List<VersionInfo> Versions, string? CompareHref)> GetVersionsWithLinksAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        var compareHref = ParseLinks(json) is { } links && links.TryGetValue("compare", out var href) ? href : null;
        return (await GetVersionsAsync(versionsHref, cancellationToken), compareHref);
    }

    public async Task<List<VersionInfo>> GetVersionsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        var list = new List<VersionInfo>();
        if (json.TryGetProperty("versions", out var arr))
        {
            foreach (var v in arr.EnumerateArray())
            {
                string? download = null;
                if (v.TryGetProperty("links", out var links))
                {
                    foreach (var l in links.EnumerateArray())
                    {
                        if (l.GetProperty("rel").GetString() == "download") { download = l.GetProperty("href").GetString(); }
                    }
                }

                list.Add(new VersionInfo(
                    v.GetProperty("id").GetGuid(),
                    v.TryGetProperty("versionNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null,
                    v.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                    v.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "",
                    download,
                    v.TryGetProperty("documentDate", out var dd) ? dd.GetString() ?? "" : "",
                    v.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetDateTimeOffset() : default,
                    v.TryGetProperty("createdByName", out var cb) ? cb.GetString() ?? "" : "",
                    Comment: v.TryGetProperty("comment", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString() : null,
                    Links: ParseLinks(v), WorkflowStatus: StrOrNull(v, "workflowStatus")));
            }
        }

        // Flag the current version = the server's CurrentVersionId pointer (issue #265), else the latest confirmed.
        Guid? pointer = json.TryGetProperty("currentVersionId", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetGuid() : null;
        var confirmed = list.Where(v => v.Status == "Confirmed").OrderByDescending(v => v.VersionNumber ?? 0).ToList();
        var currentId = pointer ?? confirmed.FirstOrDefault()?.Id;
        return confirmed.Select(v => v with { IsCurrent = v.Id == currentId }).ToList();
    }

    // Takes the version collection's advertised `compare` address; the two versions travel as query parameters,
    // because a link names ONE resource and a pair has none (issue #416, resolved by reshaping the API).
    public async Task<VersionComparison> GetVersionComparisonAsync(string compareHref, Guid fromVersionId, Guid toVersionId, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>($"{compareHref}?from={fromVersionId}&to={toVersionId}", cancellationToken);
        var lines = new List<DiffLineInfo>();
        if (json.TryGetProperty("lines", out var arr))
        {
            foreach (var l in arr.EnumerateArray())
            {
                lines.Add(new DiffLineInfo(l.GetProperty("op").GetInt32(), l.GetProperty("text").GetString() ?? ""));
            }
        }

        return new VersionComparison(json.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True, lines);
    }

    // Inline unified diff of a checked-out document's current version vs its working copy in check-out (ADR 0517).
    // Holder-only; Available=false when there's no working-copy stash or a side has no extractable text.
    public async Task<VersionComparison> GetCheckoutComparisonAsync(CheckoutItem checkout, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(RequireHref(checkout, "compare"), cancellationToken);
        var lines = new List<DiffLineInfo>();
        if (json.TryGetProperty("lines", out var arr))
        {
            foreach (var l in arr.EnumerateArray())
            {
                lines.Add(new DiffLineInfo(l.GetProperty("op").GetInt32(), l.GetProperty("text").GetString() ?? ""));
            }
        }

        return new VersionComparison(json.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True, lines);
    }

    // A specific version's bytes (via its presigned download URL) — used to stage both versions to temp files for
    // an external diff tool (Beyond Compare).
    public async Task<byte[]> DownloadVersionBytesAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        var (bytes, _) = await DownloadAsync(downloadUrl, cancellationToken);
        return bytes;
    }

    // Uploads bytes as a NEW version of an existing document (the check-in upload) — POST /versions → PUT bytes
    // → finalize. Distinct from UploadFileAsync, which creates a new document.
    public async Task UploadNewVersionAsync(Guid documentId, byte[] bytes, string fileExtension, string? comment = null, CancellationToken cancellationToken = default) =>
        await UploadNewVersionAsync(await DocumentRelAsync(documentId, "versions", cancellationToken), bytes, fileExtension, comment, cancellationToken);

    /// <summary>The same new version, posted to a versions address the caller already holds.</summary>
    /// <remarks>
    /// A caller holding the ROW — a folder listing advertises each child's <c>versions</c> rel — follows it
    /// directly instead of fetching the document again to find it (ADRs 0555/0557).
    /// </remarks>
    public async Task UploadNewVersionAsync(string versionsHref, byte[] bytes, string fileExtension, string? comment = null, CancellationToken cancellationToken = default)
    {
        // The check-in comment is the new version's "why this revision" note (ADR 0528) — set on the version
        // itself, not posted to the chat feed as it used to be.
        var versionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        using var versionResponse = await _http.PostAsJsonAsync(versionsHref, new { fileExtension, comment = versionComment }, cancellationToken);
        if (versionResponse.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("This document is checked out by another user or under a legal hold.");
        }

        versionResponse.EnsureSuccessStatusCode();
        var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var uploadUrl = version.GetProperty("uploadUrl").GetString()!;

        using var uploadContent = new ByteArrayContent(bytes);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType($"x{fileExtension}"));
        using var uploadResponse = await Anonymous.PutAsync(uploadUrl, uploadContent, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();

        using var finalizeResponse = await _http.PutAsync(RequireRel(version, "self", "The pending version"), null, cancellationToken);
        finalizeResponse.EnsureSuccessStatusCode();
    }

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".md" or ".markdown" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        ".eml" => "message/rfc822",
        ".msg" => "application/vnd.ms-outlook",
        _ => "application/octet-stream",
    };

    // Fetches a preview/download URL's bytes (a presigned URL — no auth) plus its content-type.
    public static async Task<(byte[] Bytes, string ContentType)> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await Anonymous.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    private async Task<List<T>> LoadPagedAsync<T>(string url, string arrayProperty, Func<JsonElement, T> parse, CancellationToken cancellationToken,
        Action<JsonElement>? onPage = null)
    {
        var items = new List<T>();
        string? next = url;

        while (next is not null)
        {
            var page = await _http.GetFromJsonAsync<JsonElement>(next, cancellationToken);
            onPage?.Invoke(page);
            if (page.TryGetProperty(arrayProperty, out var array))
            {
                items.AddRange(array.EnumerateArray().Select(parse));
            }

            next = FindLink(page, "next");
        }

        return items;
    }

    private static Node ParseNode(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
        item.TryGetProperty("hasVersions", out var hv) && hv.GetBoolean(),
        item.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
        item.TryGetProperty("hasReferences", out var hr) && hr.GetBoolean(),
        item.TryGetProperty("onLegalHold", out var lh) && lh.ValueKind == JsonValueKind.True,
        item.TryGetProperty("checkedOut", out var co) && co.ValueKind == JsonValueKind.True,
        item.TryGetProperty("checkedOutByMe", out var com) && com.ValueKind == JsonValueKind.True,
        item.TryGetProperty("checkedOutByName", out var con) ? con.GetString() ?? "" : "",
        // List-row columns (ADR "List-row columns and sorting").
        item.TryGetProperty("documentType", out var dt) ? dt.GetString() ?? "" : "",
        item.TryGetProperty("documentDate", out var dd) && dd.ValueKind == JsonValueKind.String && DateOnly.TryParse(dd.GetString(), out var date) ? date : null,
        item.TryGetProperty("sizeBytes", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : null,
        item.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array ? tg.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList() : [],
        item.TryGetProperty("sensitivityLabelName", out var sln) ? sln.GetString() ?? "" : "",
        item.TryGetProperty("sensitivityLabelColor", out var slc) && slc.ValueKind == JsonValueKind.String ? slc.GetString() : null,
        item.TryGetProperty("versionCount", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt32() : 0,
        item.TryGetProperty("versionCreatedAt", out var vca) && vca.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(vca.GetString(), out var vcaDt) ? vcaDt : null,
        // The row's advertised addresses. WITHOUT this every Node.Links is null and Href() throws — which
        // is exactly what shipped in 2aeaae0, because the edit that added it silently did not apply.
        ParseLinks(item));

    // rel -> href for one resource's advertised links, relative (the HttpClient has the base address).
    internal static IReadOnlyDictionary<string, string>? ParseLinks(JsonElement item)
    {
        if (!item.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in links.EnumerateArray())
        {
            if (l.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } r
                && l.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
            {
                map[r] = h.TrimStart('/');
            }
        }

        return map.Count == 0 ? null : map;
    }

    private static SearchResult ParseSearchResult(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("isFolder", out var f) && f.GetBoolean(),
        item.TryGetProperty("parentId", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetGuid() : null,
        item.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
        item.TryGetProperty("highlight", out var hl) ? hl.GetString() ?? "" : "",
        FindLink(item, "versions"),
        ParseLinks(item));

    private static ReferencingFolder ParseReferencingFolder(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "");

    private static Reference ParseReference(JsonElement item) => new(
        item.GetProperty("referenceId").GetGuid(),
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
        item.TryGetProperty("hasVersions", out var hv) && hv.GetBoolean(),
        item.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
        item.TryGetProperty("hasReferences", out var hr) && hr.GetBoolean(),
        item.TryGetProperty("realParentId", out var rp) && rp.ValueKind != JsonValueKind.Null ? rp.GetGuid() : null,
        RelHref(item, "delete"));

    private static RecycleBinItem ParseRecycleBinItem(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.GetProperty("deletedAt").GetDateTimeOffset(),
        ParseLinks(item));

    private static Comment ParseComment(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.TryGetProperty("parentMessageId", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetGuid() : null,
        item.GetProperty("body").GetString() ?? "",
        item.TryGetProperty("authorName", out var a) ? a.GetString() ?? "" : "",
        item.GetProperty("createdAt").GetDateTimeOffset(),
        RelHref(item, "author-card"),
        item.TryGetProperty("kind", out var k) ? k.GetInt32() : 0,
        item.TryGetProperty("versionNumber", out var vn) && vn.ValueKind != JsonValueKind.Null ? vn.GetInt32() : null,
        item.TryGetProperty("versionComment", out var vc) && vc.ValueKind != JsonValueKind.Null ? vc.GetString() : null,
        item.TryGetProperty("versionCommentKind", out var vck) && vck.ValueKind != JsonValueKind.Null ? vck.GetInt32() : null,
        ParseMentions(item));

    private static IReadOnlyList<Mention> ParseMentions(JsonElement item)
    {
        if (!item.TryGetProperty("mentions", out var mentions) || mentions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. mentions.EnumerateArray().Select(m => new Mention(
            m.GetProperty("userId").GetGuid(),
            m.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : ""))];
    }

    // The href a resource advertises for a rel, or null when it doesn't offer one. A missing rel is meaningful —
    // it means "not available here" — so callers branch on null rather than composing a URL (ADR 0543).
    // internal: InboxApi follows rels too, since the inbox calls moved there (#443 direction).
    internal static string? RelHref(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.TryGetProperty("rel", out var r) && r.GetString() == rel
                && link.TryGetProperty("href", out var h) && h.GetString() is { Length: > 0 } href)
            {
                return href.TrimStart('/');
            }
        }

        return null;
    }

    // The root document, fetched once per client instance. Cached because the root is a constant for a session:
    // re-reading it before every call would turn one request into two, which is the usual reason a codebase
    // abandons hypermedia and goes back to composing paths (issue #416).
    private IReadOnlyDictionary<string, string>? _rootLinks;
    private readonly SemaphoreSlim _rootGate = new(1, 1);

    // The caller's own "me" resource, cached for the same reason as the root. Everything about the signed-in
    // account hangs off it — password, photo, MFA, passkeys, WebDAV password, personal repository, notification
    // preferences — so this is the desktop's counterpart to the web client's MeHrefAsync (issue #416). Without
    // it every one of those was a composed /api/users/me/… path, which is thirteen private routes copied into a
    // second codebase.
    private IReadOnlyDictionary<string, string>? _meLinks;
    private string? _myEmail;
    private readonly SemaphoreSlim _meGate = new(1, 1);

    /// <summary>
    /// The caller's own email address, or <c>null</c> for a principal with no personal account.
    /// </summary>
    /// <remarks>
    /// Comes from the same "me" read the rels do, so a profile screen showing who you are signed in as costs no
    /// request of its own (#464).
    /// </remarks>
    public async Task<string?> MyEmailAsync(CancellationToken cancellationToken = default)
    {
        // Any rel will do: resolving one populates the whole document, email included.
        await MeHrefAsync("self", cancellationToken);
        return _myEmail;
    }

    /// <summary>
    /// The href for a rel on the caller's own "me" resource. Throws when it is not advertised.
    /// </summary>
    public async Task<string> MeHrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        if (_meLinks is null)
        {
            // Resolve the root href BEFORE taking the gate: these are separate semaphores, but taking one while
            // holding the other is how the web client deadlocked its whole workbench (ADR 0543 notes), so keep
            // the acquisition order trivially safe by not nesting at all.
            var meHref = await RootHrefAsync("me", cancellationToken);

            await _meGate.WaitAsync(cancellationToken);
            try
            {
                if (_meLinks is null)
                {
                    var me = await _http.GetFromJsonAsync<JsonElement>(meHref, cancellationToken);
                    _meLinks = ParseLinks(me) ?? new Dictionary<string, string>();

                    // The email rides in the SAME response as the links (#464) — reading it here rather than
                    // adding a second call is ADR 0557's rule applied to a value, not an address: one read,
                    // everything it carried.
                    _myEmail = me.TryGetProperty("email", out var email) && email.ValueKind is JsonValueKind.String
                        ? email.GetString()
                        : null;
                }
            }
            finally
            {
                _meGate.Release();
            }
        }

        return _meLinks.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The 'me' resource does not advertise the '{rel}' rel.");
    }

    /// <summary>
    /// The href for a root-level rel. Throws when the server does not advertise it — for the collections a screen
    /// is built around, a null would surface as an empty list ("you have no tags") rather than as a fault.
    /// </summary>
    public async Task<string> RootHrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        if (_rootLinks is null)
        {
            await _rootGate.WaitAsync(cancellationToken);
            try
            {
                _rootLinks ??= await GetRootLinksAsync(cancellationToken);
            }
            finally
            {
                _rootGate.Release();
            }
        }

        return _rootLinks.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The API root does not advertise the '{rel}' rel.");
    }

    // The API root's link relations — the ONE URL a client is allowed to know (ADR 0543); everything else is
    // discovered from here. Note "api" carries no slash, so it is not a composed resource path.
    public async Task<IReadOnlyDictionary<string, string>> GetRootLinksAsync(CancellationToken cancellationToken = default)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        using var response = await _http.GetAsync("api", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return links;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("links", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in items.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } name
                    && link.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } value)
                {
                    links[name] = value.TrimStart('/');
                }
            }
        }

        return links;
    }

    // ---- External links (ADR 0546) ---------------------------------------------------------------------

    public sealed record ExternalLinkInfo(
        Guid Id, Guid DocumentId, string DocumentName, string? Url, DateTimeOffset ExpiresAt,
        int? MaxAccesses, int AccessCount, string CreatedByName, bool CanExtend, string Etag,
        string? RevokeHref, string? AvailabilityHref, Guid? ParentId, string? RevealUrlHref = null)
    {
        /// <summary>
        /// The expiry as the READER experiences it. The server sends UTC; the row used to format that raw while
        /// the detail dialog called ToLocalTime(), so one link showed two times an hour apart outside UTC — the
        /// list said 20:40 and its own detail said 21:40 (the bug report this fixes). One property now, used by
        /// both, so they cannot drift again.
        /// </summary>
        public string ExpiresLocal => ExpiresAt.ToLocalTime().ToString("g");
    }

    public sealed record ExternalLinkListInfo(IReadOnlyList<ExternalLinkInfo> Links, bool CanCreate, bool CanViewOthers);

    // Follows the href the document resource advertised via its "external-links" rel (ADR 0543) — never composed.
    public async Task<ExternalLinkListInfo> GetExternalLinksAsync(string linksHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(linksHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ExternalLinkListInfo([], false, false);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseLinkList(doc.RootElement);
    }

    // The caller's own links across all documents; a tenant admin may filter by user or group.
    public async Task<ExternalLinkListInfo> GetMyExternalLinksAsync(
        string linksHref, Guid? userId = null, Guid? groupId = null, CancellationToken cancellationToken = default)
    {
        var query = groupId is { } g ? $"?groupId={g}" : userId is { } u ? $"?userId={u}" : "";
        return await GetExternalLinksAsync(linksHref + query, cancellationToken);
    }

    // Returns the created link — the ONLY time its URL is available, since the list endpoints never return the
    // token (ADR 0546). Null when the tenant has the feature switched off or the caller lacks the right.
    // Returns null ONLY when the share was refused — the tenant switch is off, or the caller lacks the right.
    // Anything else throws, so the dialog reports a real failure as one. Collapsing every non-success into null
    // is what made a 500 (a non-UTC expiry Postgres refused to store) display as "external links are switched off
    // for this tenant": a message that sent the reader to a setting that was already correct.
    public async Task<ExternalLinkInfo?> CreateExternalLinkAsync(
        string linksHref, DateTimeOffset? expiresAt, int? maxAccesses, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(linksHref, new { expiresAt, maxAccesses }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return null;
            }

            throw new ApiActionException($"The link could not be created ({(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseLink(doc.RootElement);
    }

    public async Task<bool> RevokeExternalLinkAsync(string revokeHref, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, revokeHref);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // Days are measured from TODAY by the server, not added onto what remains, and the access cap travels in the
    // same call — a link out of both time and accesses is only half-renewed by moving either alone (ADR 0546).
    // maxAccesses null means unlimited; the server takes both in one request so they cannot land apart.
    public async Task<bool> RenewExternalLinkAsync(
        string availabilityHref, int days, int? maxAccesses, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, availabilityHref)
        {
            Content = JsonContent.Create(new { days, maxAccesses }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static ExternalLinkListInfo ParseLinkList(JsonElement root)
    {
        var links = new List<ExternalLinkInfo>();
        if (root.TryGetProperty("externalLinks", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            links.AddRange(items.EnumerateArray().Select(ParseLink));
        }

        return new ExternalLinkListInfo(
            links,
            root.TryGetProperty("canCreate", out var c) && c.ValueKind == JsonValueKind.True,
            root.TryGetProperty("canViewOthers", out var v) && v.ValueKind == JsonValueKind.True);
    }

    private static ExternalLinkInfo ParseLink(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.TryGetProperty("documentId", out var d) ? d.GetGuid() : Guid.Empty,
        item.TryGetProperty("documentName", out var dn) ? dn.GetString() ?? "" : "",
        item.TryGetProperty("url", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() : null,
        item.GetProperty("expiresAt").GetDateTimeOffset(),
        item.TryGetProperty("maxAccesses", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetInt32() : null,
        item.TryGetProperty("accessCount", out var a) ? a.GetInt32() : 0,
        item.TryGetProperty("createdByName", out var cb) ? cb.GetString() ?? "" : "",
        item.TryGetProperty("canExtend", out var ce) && ce.ValueKind == JsonValueKind.True,
        item.TryGetProperty("etag", out var e) ? e.GetString() ?? "" : "",
        RelHref(item, "revoke"),
        RelHref(item, "availability"),
        // Null in the per-document list, which is already sitting on the document — "Go to" only means something
        // in the cross-document one, where a row is the reader's only handle on where the thing lives.
        item.TryGetProperty("parentId", out var pid) && pid.ValueKind != JsonValueKind.Null ? pid.GetGuid() : null,
        // The tenant's opt-in to revealing an existing link's URL (issue #412), as the server states it: the rel
        // is advertised only where ShowExternalLinkUrl is on, so its ABSENCE is what makes "not shown" truthful
        // (ADR 0543). The desktop ignored it entirely and always claimed the URL was unavailable.
        RelHref(item, "reveal-url"));

    /// <summary>
    /// An existing link's URL, fetched on demand by FOLLOWING the row's advertised <c>reveal-url</c> (ADR 0543).
    /// </summary>
    /// <remarks>
    /// Deliberately not carried by the listing: the token travels only when somebody asks for this one link,
    /// which is what keeps it out of the page every row arrived on. Null when the fetch fails, so the caller
    /// leaves the note as it was rather than showing an empty "URL:".
    /// </remarks>
    public async Task<string?> RevealExternalLinkUrlAsync(string revealHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(revealHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
    }

    // Fetch an author's identity card by FOLLOWING the href the message advertised (ADR 0544).
    public async Task<(UserCard Card, byte[]? Photo)?> GetUserCardAsync(string cardHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(cardHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var card = new UserCard(
            root.GetProperty("displayName").GetString() ?? "",
            root.GetProperty("email").GetString() ?? "",
            root.TryGetProperty("isActive", out var active) && active.GetBoolean(),
            RelHref(root, "photo"));

        // The photo rel is present only when one exists, so this never probes for a 404. The endpoint is
        // bearer-protected, so the bytes must come through the authenticated client.
        byte[]? photo = null;
        if (card.PhotoHref is { } photoHref)
        {
            try
            {
                photo = await _http.GetByteArrayAsync(photoHref, cancellationToken);
            }
            catch (HttpRequestException)
            {
                photo = null;
            }
        }

        return (card, photo);
    }

    // ---- Workflow + tasks (ADR "Workflow / document state model", 0009) -----------------------------------

    public async Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("tasks", cancellationToken), cancellationToken);
        var list = new List<TaskInfo>();
        if (json.TryGetProperty("tasks", out var tasks))
        {
            foreach (var t in tasks.EnumerateArray())
            {
                list.Add(new TaskInfo(
                    t.GetProperty("documentId").GetGuid(),
                    t.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
                    t.GetProperty("versionId").GetGuid(),
                    t.GetProperty("documentName").GetString() ?? "",
                    t.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : null,
                    t.TryGetProperty("assignedAt", out var a) ? a.GetDateTimeOffset() : default,
                    ParseLinks(t)));
            }
        }

        return list;
    }

    // The latest confirmed version's workflow (null if the document has no confirmed version).
    public async Task<WorkflowInfo?> GetWorkflowAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "versions", cancellationToken), cancellationToken);
        if (!response.TryGetProperty("versions", out var versions))
        {
            return null;
        }

        JsonElement? latest = null;
        var number = -1;
        foreach (var v in versions.EnumerateArray())
        {
            if (v.GetProperty("status").GetString() != "Confirmed")
            {
                continue;
            }

            var n = v.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : 0;
            if (n >= number)
            {
                number = n;
                latest = v;
            }
        }

        if (latest is not { } cur || FindLink(cur, "workflow") is not { } wfLink)
        {
            return null;
        }

        var json = await _http.GetFromJsonAsync<JsonElement>(wfLink.TrimStart('/'), cancellationToken);
        var links = new Dictionary<string, string>();
        if (json.TryGetProperty("links", out var ls))
        {
            foreach (var l in ls.EnumerateArray())
            {
                links[l.GetProperty("rel").GetString() ?? ""] = l.GetProperty("href").GetString() ?? "";
            }
        }

        var history = new List<WorkflowTransitionInfo>();
        if (json.TryGetProperty("history", out var hs))
        {
            foreach (var h in hs.EnumerateArray())
            {
                history.Add(new WorkflowTransitionInfo(
                    h.GetProperty("toStatusName").GetString() ?? "",
                    StrOrNull(h, "assignedToName"), StrOrNull(h, "performedByName"), StrOrNull(h, "rejectionReason")));
            }
        }

        return new WorkflowInfo(
            json.GetProperty("status").GetInt32(),
            json.GetProperty("statusName").GetString() ?? "",
            StrOrNull(json, "assignedToName"), history, links);
    }

    // POSTs a workflow transition action (the href comes from WorkflowInfo.Links). Throws ApiActionException
    // with the server's Problem-Details detail on a rejected transition (409/400/403).
    public async Task PostWorkflowActionAsync(string href, object? body, CancellationToken cancellationToken = default)
    {
        using var response = body is null
            ? await _http.PostAsync(href.TrimStart('/'), null, cancellationToken)
            : await _http.PostAsJsonAsync(href.TrimStart('/'), body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = ApiErrorText.For(null);
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (problem.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code)
                {
                    detail = ApiErrorText.For(code);
                }
            }
            catch (Exception) { /* keep the generic localised message */ }

            throw new ApiActionException(detail);
        }
    }

    // The candidate reviewers for submitting a document into the workflow (ADR "Workflow assignable-reviewers
    // endpoint") — a light per-document catalog any editor can read, no CanManageUsers needed. Returns empty on
    // no access (e.g. the caller lacks CanEditContent).
    public async Task<IReadOnlyList<UserOptionInfo>> GetAssignableReviewersAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "assignable-reviewers", cancellationToken), cancellationToken);
            var list = new List<UserOptionInfo>();
            if (json.TryGetProperty("reviewers", out var reviewers))
            {
                foreach (var u in reviewers.EnumerateArray())
                {
                    list.Add(new UserOptionInfo(u.GetProperty("id").GetGuid(), u.GetProperty("displayName").GetString() ?? ""));
                }
            }

            return list;
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    // ---- Users & groups administration (ADR "Users & groups administration tab") --------------------

    public async Task<List<PrincipalInfo>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        await LoadPagedAsync(await RootHrefAsync("users", cancellationToken), "users", ParseUser, cancellationToken);

    public async Task<List<PrincipalInfo>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        await LoadPagedAsync(await RootHrefAsync("groups", cancellationToken), "groups", ParseGroup, cancellationToken);

    /// <summary>
    /// Creates a user and returns the created ROW — not its id. The create response is the resource, rels
    /// included, so a caller that goes on to act on what it created already holds the addresses (ADR 0555).
    /// </summary>
    public async Task<PrincipalInfo> CreateUserAsync(string email, string displayName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await RootHrefAsync("users", cancellationToken), new { email, displayName }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("A user with this email already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage users.");
        }

        response.EnsureSuccessStatusCode();
        return ParseUser(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task<PrincipalInfo> CreateGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await RootHrefAsync("groups", cancellationToken), new { name }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A group named '{name}' already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage groups.");
        }

        response.EnsureSuccessStatusCode();
        return ParseGroup(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    // Follows a rel off a resource the client just READ or just CREATED — the case where the address is already
    // in hand and only needs picking up, as opposed to DocumentRelAsync's "I hold an id, fetch the resource".
    private static string RequireRel(JsonElement resource, string rel, string what) =>
        ParseLinks(resource) is { } links && links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"{what} advertised no '{rel}' rel (ADR 0543).");

    private static string RequireHref(IAdvertisesLinks row, string rel) =>
        row.Href(rel)
        ?? throw new InvalidOperationException($"The row '{row.Name}' advertised no '{rel}' rel (ADR 0543/0555).");

    private static string RequireHref(InboxItemInfo item, string rel) =>
        item.Href(rel)
        ?? throw new InvalidOperationException($"The inbox item '{item.Name}' advertised no '{rel}' rel (ADR 0543/0555).");

    private static string RequireHref(LegalHoldInfo hold, string rel) =>
        hold.Href(rel)
        ?? throw new InvalidOperationException($"The legal hold '{hold.Name}' advertised no '{rel}' rel — a RELEASED hold offers neither release nor add-item (ADR 0543/0555).");

    private static string RequireHref(CheckoutItem checkout, string rel) =>
        checkout.Href(rel)
        ?? throw new InvalidOperationException($"The check-out on '{checkout.Name}' advertised no '{rel}' rel — `compare` is absent with no stash to diff (ADR 0543/0555).");

    private static string RequireHref(NotificationInfo notification, string rel) =>
        notification.Href(rel)
        ?? throw new InvalidOperationException($"The notification row advertised no '{rel}' rel (ADR 0543/0555).");

    private static string RequireHref(RetentionItemInfo item, string rel) =>
        item.Href(rel)
        ?? throw new InvalidOperationException($"'{item.DocumentName}' advertised no '{rel}' rel — a hold or a required review withholds it (ADR 0543/0555).");

    private static string RequireHref(VersionInfo version, string rel) =>
        version.Href(rel)
        ?? throw new InvalidOperationException($"Version {version.VersionNumber} advertised no '{rel}' rel — only a confirmed version offers one (ADR 0543/0555).");

    private static string RequireHref(ReminderInfo reminder, string rel) =>
        reminder.Href(rel)
        ?? throw new InvalidOperationException($"The reminder row advertised no '{rel}' rel (ADR 0543/0555).");

    private static string RequireHref(TagCatalogItem tag, string rel) =>
        tag.Href(rel)
        ?? throw new InvalidOperationException($"The tag '{tag.Name}' advertised no '{rel}' rel (ADR 0543/0555).");

    private static string RequireHref(SavedSearchInfo search, string rel) =>
        search.Href(rel)
        ?? throw new InvalidOperationException($"The saved search advertised no '{rel}' rel — it is not yours to change (ADR 0543/0555).");

    private static string RequireHref(ServiceAccountInfo account, string rel) =>
        account.Href(rel)
        ?? throw new InvalidOperationException($"The service account advertised no '{rel}' rel — a revoked account offers none (ADR 0543/0555).");

    private static string RequireHref(PrincipalInfo principal, string rel) =>
        principal.Href(rel)
        ?? throw new InvalidOperationException($"The {(principal.IsGroup ? "group" : "user")} row advertised no '{rel}' rel (ADR 0543/0555).");

    /// <summary>Sets a principal's system rights at the address its own row advertised.</summary>
    public Task SetRightsAsync(PrincipalInfo principal, SystemRightsData rights, CancellationToken cancellationToken = default) =>
        SetRightsCoreAsync(RequireHref(principal, "rights"), rights, cancellationToken);

    private async Task SetRightsCoreAsync(string path, SystemRightsData rights, CancellationToken cancellationToken)
    {
        using var response = await _http.PutAsJsonAsync(path, rights, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You can only grant rights you hold yourself; changing tenant-admin needs a tenant admin.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Deactivates a user (reversible on the server; the row stays, marked inactive).
    // Deactivates a user. If they still hold pending review tasks, the server refuses (409
    // REVIEWER_HAS_PENDING_REVIEWS) unless reassignReviewsTo hands them to a replacement reviewer (ADR
    // "Workflow review reassignment") — surfaced as ReviewerHasPendingReviewsException so the caller can prompt.
    public async Task DeleteUserAsync(PrincipalInfo user, Guid? reassignReviewsTo = null, CancellationToken cancellationToken = default)
    {
        // The reassignment is a QUERY on the advertised address, not a path this client invents.
        var deactivateHref = RequireHref(user, "deactivate");
        var url = reassignReviewsTo is { } r ? $"{deactivateHref}?reassignReviewsTo={r}" : deactivateHref;
        using var response = await _http.DeleteAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage users.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict && await ErrorCodeAsync(response, cancellationToken) == "REVIEWER_HAS_PENDING_REVIEWS")
        {
            throw new ReviewerHasPendingReviewsException("This user still holds pending review tasks.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return json.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // Deletes a group (409 if it still has child groups or members).
    public async Task DeleteGroupAsync(PrincipalInfo group, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(RequireHref(group, "delete"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("The group still has child groups or members.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage groups.");
        }

        response.EnsureSuccessStatusCode();
    }

    // ---- Service accounts (ADR 0203/0534) -----------------------------------------------------------

    public async Task<List<ServiceAccountInfo>> GetServiceAccountsAsync(CancellationToken cancellationToken = default) =>
        await LoadPagedAsync(await RootHrefAsync("serviceAccounts", cancellationToken), "serviceAccounts", ParseServiceAccount, cancellationToken);

    // Create a service account with its rights; returns the one-time client_id + client_secret (shown once).
    public async Task<ServiceAccountSecret> CreateServiceAccountAsync(string name, SystemRightsData rights, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await RootHrefAsync("serviceAccounts", cancellationToken), ToServiceAccountBody(name, rights), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A service account named '{name}' already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You can only grant rights you hold yourself.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new ServiceAccountSecret(json.GetProperty("clientId").GetString() ?? "", json.GetProperty("clientSecret").GetString() ?? "");
    }

    // Edit an existing account's name + rights (PUT, ADR 0534) — escalation-capped server-side like create.
    public async Task UpdateServiceAccountAsync(ServiceAccountInfo account, string name, SystemRightsData rights, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(RequireHref(account, "edit"), ToServiceAccountBody(name, rights), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A service account named '{name}' already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You can only grant rights you hold yourself.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Rotate the secret — mints a new client_secret and invalidates the old one; returns the one-time secret.
    public async Task<ServiceAccountSecret> RotateServiceAccountSecretAsync(ServiceAccountInfo account, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(account, "rotate-secret"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage service accounts.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new ServiceAccountSecret(json.GetProperty("clientId").GetString() ?? "", json.GetProperty("clientSecret").GetString() ?? "");
    }

    // Revoke — one-way, sets IsActive = false; the credentials stop working immediately.
    public async Task RevokeServiceAccountAsync(ServiceAccountInfo account, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(RequireHref(account, "revoke"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage service accounts.");
        }

        response.EnsureSuccessStatusCode();
    }

    // The create/update body — the five grantable rights, camelCase over the wire (name + booleans).
    private static object ToServiceAccountBody(string name, SystemRightsData rights) => new
    {
        name,
        canManageRepositories = rights.CanManageRepositories,
        canManageMasks = rights.CanManageMasks,
        canManageServiceAccounts = rights.CanManageServiceAccounts,
        canImport = rights.CanImport,
        canExport = rights.CanExport,
    };

    private static ServiceAccountInfo ParseServiceAccount(JsonElement e)
    {
        bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new ServiceAccountInfo(
            e.GetProperty("id").GetGuid(),
            e.GetProperty("name").GetString() ?? "",
            e.TryGetProperty("clientId", out var c) ? c.GetString() ?? "" : "",
            !e.TryGetProperty("isActive", out var a) || a.ValueKind == JsonValueKind.True,
            B("canManageRepositories"), B("canManageMasks"), B("canManageServiceAccounts"), B("canImport"), B("canExport"),
            ParseLinks(e));
    }

    // ---- Passwords (ADR "User password management") -------------------------------------------------

    public async Task ChangeMyPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(await MeHrefAsync("changePassword", cancellationToken), new { currentPassword, newPassword }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("The current password is incorrect.");
        }

        response.EnsureSuccessStatusCode();
    }

    // WebDAV gateway (ADR "WebDAV gateway") — the app-specific WebDAV password + mount info.
    public sealed record WebDavStatus(bool Enabled, string Username, string Url, string? Password);

    public async Task<WebDavStatus> GetWebDavStatusAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<WebDavStatus>(await MeHrefAsync("webdavPassword", cancellationToken), cancellationToken) ?? new WebDavStatus(false, "", "", null);

    // Generate/regenerate — returns the plaintext password (shown once).
    public async Task<WebDavStatus> GenerateWebDavPasswordAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await MeHrefAsync("webdavPassword", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WebDavStatus>(cancellationToken))!;
    }

    public async Task RevokeWebDavPasswordAsync(CancellationToken cancellationToken = default) =>
        (await _http.DeleteAsync(await MeHrefAsync("webdavPassword", cancellationToken), cancellationToken)).EnsureSuccessStatusCode();

    // Admin reset — returns the generated password (shown once).
    public async Task<string> ResetUserPasswordAsync(PrincipalInfo user, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(user, "reset-password"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to reset passwords.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("password").GetString() ?? "";
    }

    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") ----------------------------

    public sealed record MfaEnrollInfo(string Secret, string OtpauthUri, string QrDataUrl);

    // Starts enrollment: returns the secret + otpauth URI + QR data URL (the secret is stored server-side as
    // a pending, not-yet-active enrollment).
    public async Task<MfaEnrollInfo> EnrollMfaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(await MeHrefAsync("mfaEnroll", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new MfaEnrollInfo(
            json.GetProperty("secret").GetString() ?? "",
            json.GetProperty("otpauthUri").GetString() ?? "",
            json.GetProperty("qrDataUrl").GetString() ?? "");
    }

    // Confirms enrollment with a code; returns the one-time recovery codes (shown once).
    public async Task<List<string>> EnableMfaAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await MeHrefAsync("mfaEnable", cancellationToken), new { code }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("That authentication code isn't right.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
    }

    public async Task DisableMfaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(await MeHrefAsync("mfa", cancellationToken), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Admin reset — disables a locked-out user's two-factor.
    public async Task ResetUserMfaAsync(PrincipalInfo user, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(user, "reset-mfa"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to reset two-factor authentication.");
        }

        response.EnsureSuccessStatusCode();
    }

    // ---- Passkeys (ADR "Desktop passkey management") ------------------------------------------------
    // List + remove are plain API calls the native app makes directly; registration needs a browser
    // attestation ceremony and is delegated to the system browser (see OidcLoopbackAuthenticator).

    // RemoveHref is the row's own `self` rel: a passkey addresses itself, so removing one follows a link the
    // list already carried instead of rebuilding /users/me/passkeys/{id} from an id (issue #416).
    public sealed record PasskeyInfo(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, string? RemoveHref = null);

    public async Task<List<PasskeyInfo>> GetPasskeysAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await MeHrefAsync("passkeys", cancellationToken), cancellationToken);
        var list = new List<PasskeyInfo>();
        if (json.TryGetProperty("passkeys", out var passkeys))
        {
            foreach (var p in passkeys.EnumerateArray())
            {
                var links = ParseLinks(p);
                list.Add(new PasskeyInfo(
                    p.GetProperty("id").GetGuid(),
                    p.GetProperty("name").GetString() ?? "",
                    p.GetProperty("createdAt").GetDateTimeOffset(),
                    p.TryGetProperty("lastUsedAt", out var lu) && lu.ValueKind != JsonValueKind.Null ? lu.GetDateTimeOffset() : null,
                    links is not null && links.TryGetValue("self", out var removeHref) ? removeHref : null));
            }
        }

        return list;
    }

    /// <summary>Removes a passkey by the address its own row advertised (its `self` rel).</summary>
    public async Task RemovePasskeyAsync(string removeHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(removeHref, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- Notification email preferences (ADR "Notification preferences") -----------------------------

    public sealed record NotificationPreferenceInfo(int Type, string TypeName, bool EmailEnabled);

    public async Task<List<NotificationPreferenceInfo>> GetNotificationPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await MeHrefAsync("notificationPreferences", cancellationToken), cancellationToken);
        var list = new List<NotificationPreferenceInfo>();
        if (json.TryGetProperty("preferences", out var prefs))
        {
            foreach (var p in prefs.EnumerateArray())
            {
                list.Add(new NotificationPreferenceInfo(
                    p.GetProperty("type").GetInt32(),
                    p.GetProperty("typeName").GetString() ?? "",
                    p.GetProperty("emailEnabled").GetBoolean()));
            }
        }

        return list;
    }

    public async Task SetNotificationPreferencesAsync(IEnumerable<NotificationPreferenceInfo> preferences, CancellationToken cancellationToken = default)
    {
        var body = new { preferences = preferences.Select(p => new { type = p.Type, emailEnabled = p.EmailEnabled }) };
        using var response = await _http.PutAsJsonAsync(await MeHrefAsync("notificationPreferences", cancellationToken), body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- In-app notifications viewer (ADR "Notification viewer + click-through") ---------------------

    // A notification row, carrying its own `read` address (ADR 0543/0555) — an already-read one advertises
    // none, so "can this be marked read" is the server's answer rather than an IsRead flag re-interpreted here.
    public sealed record NotificationInfo(Guid Id, string Type, string Title, string Body, Guid? DocumentId, Guid? DocumentParentId, DateTimeOffset CreatedAt, bool IsRead, int EventCount = 1, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // ReadAllHref is the collection's own `read-all`; null when the server did not offer it.
    public sealed record NotificationList(IReadOnlyList<NotificationInfo> Items, int UnreadCount, string? ReadAllHref = null);

    public async Task<NotificationList> GetNotificationsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("notifications", cancellationToken), cancellationToken);
        var items = new List<NotificationInfo>();
        if (json.TryGetProperty("notifications", out var arr))
        {
            foreach (var n in arr.EnumerateArray())
            {
                items.Add(new NotificationInfo(
                    n.GetProperty("id").GetGuid(),
                    n.GetProperty("type").GetString() ?? "",
                    n.GetProperty("title").GetString() ?? "",
                    n.GetProperty("body").GetString() ?? "",
                    n.TryGetProperty("documentId", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetGuid() : null,
                    n.TryGetProperty("documentParentId", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetGuid() : null,
                    n.GetProperty("createdAt").GetDateTimeOffset(),
                    n.TryGetProperty("isRead", out var r) && r.ValueKind == JsonValueKind.True,
                    n.TryGetProperty("eventCount", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 1,
                    ParseLinks(n)));
            }
        }

        return new NotificationList(
            items,
            json.TryGetProperty("unreadCount", out var uc) ? uc.GetInt32() : 0,
            ParseLinks(json) is { } links && links.TryGetValue("read-all", out var readAll) ? readAll : null);
    }

    /// <summary>Marks one notification read at the address its own row advertised (ADR 0555).</summary>
    public async Task MarkNotificationReadAsync(NotificationInfo notification, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(notification, "read"), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Marks everything read at the collection's own `read-all` address (ADR 0555).</summary>
    public async Task MarkAllNotificationsReadAsync(string readAllHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(readAllHref, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- Legal holds (ADR "Legal hold & retention enforcement") -------------------------------------

    // A hold, carrying the addresses its own row advertised (ADR 0543/0555): `self`, plus `release`/`add-item`
    // only while it is active — a released hold offers neither, so the affordance is the server's answer.
    public sealed record LegalHoldInfo(Guid Id, string Name, string? Reason, DateTimeOffset PlacedAt, bool IsActive, int ItemCount, List<LegalHoldItemInfo> Items, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // A covered document. RemoveHref is the pairing's own address — the item is the only thing that knows both
    // ends of it — and is null once the hold is released.
    public sealed record LegalHoldItemInfo(Guid DocumentId, string DocumentName, string? RemoveHref = null, Guid? ParentId = null);

    public async Task<List<LegalHoldInfo>> GetLegalHoldsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("legalHolds", cancellationToken), cancellationToken);
        var list = new List<LegalHoldInfo>();
        if (json.TryGetProperty("holds", out var holds))
        {
            foreach (var h in holds.EnumerateArray())
            {
                list.Add(ParseLegalHold(h));
            }
        }

        return list;
    }

    public async Task<LegalHoldInfo> GetLegalHoldAsync(LegalHoldInfo hold, CancellationToken cancellationToken = default) =>
        ParseLegalHold(await _http.GetFromJsonAsync<JsonElement>(RequireHref(hold, "self"), cancellationToken));

    public async Task<LegalHoldInfo> CreateLegalHoldAsync(string name, string? reason, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(await RootHrefAsync("legalHolds", cancellationToken), new { name, reason }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to place legal holds.");
        }

        response.EnsureSuccessStatusCode();
        return ParseLegalHold(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task AddLegalHoldItemAsync(LegalHoldInfo hold, Guid documentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(hold, "add-item"), new { documentId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("The document is already on this hold.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveLegalHoldItemAsync(LegalHoldItemInfo item, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(
            item.RemoveHref ?? throw new InvalidOperationException($"'{item.DocumentName}' advertised no 'remove' rel — a released hold offers none (ADR 0543/0555)."),
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task ReleaseLegalHoldAsync(LegalHoldInfo hold, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(hold, "release"), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static LegalHoldInfo ParseLegalHold(JsonElement e)
    {
        var items = new List<LegalHoldItemInfo>();
        if (e.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in itemsEl.EnumerateArray())
            {
                items.Add(new LegalHoldItemInfo(i.GetProperty("documentId").GetGuid(), i.GetProperty("documentName").GetString() ?? "", RelHref(i, "remove"), i.TryGetProperty("parentId", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetGuid() : null));
            }
        }

        return new LegalHoldInfo(
            e.GetProperty("id").GetGuid(),
            e.GetProperty("name").GetString() ?? "",
            e.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            e.GetProperty("placedAt").GetDateTimeOffset(),
            e.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True,
            e.TryGetProperty("itemCount", out var c) ? c.GetInt32() : items.Count,
            items,
            ParseLinks(e));
    }

    // ---- Retention schedule (ADR "Retention policies (auto-disposition)") ---------------------------

    // A scheduled document. `dispose` is CONDITIONAL server-side — absent while a review is required or a hold
    // suspends it — so the row's own links are what decide whether the action is offered (ADR 0543/0555).
    public sealed record RetentionItemInfo(Guid DocumentId, string DocumentName, int RetentionYears, string DispositionDate, bool Overdue, bool SuspendedByHold, string? RetentionOverrideUntil, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }
    public sealed record RetentionScheduleInfo(IReadOnlyList<RetentionItemInfo> Items, bool RequiresReview);

    public async Task<RetentionScheduleInfo> GetRetentionScheduleAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>(await RootHrefAsync("retentionSchedule", cancellationToken), cancellationToken);
        var list = new List<RetentionItemInfo>();
        if (json.TryGetProperty("items", out var items))
        {
            foreach (var i in items.EnumerateArray())
            {
                list.Add(new RetentionItemInfo(
                    i.GetProperty("documentId").GetGuid(),
                    i.GetProperty("documentName").GetString() ?? "",
                    i.TryGetProperty("retentionYears", out var y) ? y.GetInt32() : 0,
                    i.GetProperty("dispositionDate").GetString() ?? "",
                    i.TryGetProperty("overdue", out var o) && o.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("suspendedByHold", out var s) && s.ValueKind == JsonValueKind.True,
                    i.TryGetProperty("retentionOverrideUntil", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null,
                    ParseLinks(i)));
            }
        }

        return new RetentionScheduleInfo(list, json.TryGetProperty("requiresReview", out var rr) && rr.ValueKind == JsonValueKind.True);
    }

    // Manually dispose an eligible document (ADR "Retention review-before-disposition").
    public async Task DisposeRetentionAsync(RetentionItemInfo item, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(RequireHref(item, "dispose"), null, cancellationToken);
        await ThrowIfProblemAsync(response, "Could not dispose the document.", cancellationToken);
    }

    // Extend a document's retention to a new "retain until" date ("yyyy-MM-dd").
    public async Task ExtendRetentionAsync(RetentionItemInfo item, string until, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(item, "extend"), new { until }, cancellationToken);
        await ThrowIfProblemAsync(response, "Could not extend retention.", cancellationToken);
    }

    // ---- Document ACL / Manage access (ADR "Manage-access UI for document/folder ACLs") -------------

    public sealed record AclRights(
        bool CanSee, bool CanReadContent, bool CanEditContent, bool CanEditIndexData,
        bool CanCreateSubItems, bool CanDelete, bool CanMove, bool CanAnnotate, bool CanManagePermissions);

    // Both rows carry the address the WRITE goes to — an existing entry advertises `edit`/`remove`, a principal
    // you may newly grant to advertises `grant`. Same shape, so the write is expressed once (ADR 0543/0555).
    public sealed record AclEntryInfo(string PrincipalType, Guid PrincipalId, AclRights Rights,
        IReadOnlyDictionary<string, string>? Links = null) : IAdvertisesLinks
    {
        public string Name => $"{PrincipalType}/{PrincipalId}";

        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    public sealed record GrantablePrincipalInfo(string Type, Guid Id, string Name,
        IReadOnlyDictionary<string, string>? Links = null) : IAdvertisesLinks
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // Everything the Manage-access dialog needs in one load. Forbidden = the caller lacks CanManagePermissions
    // (the list/picker endpoints 403), so the dialog shows a read-only message instead of a broken editor.
    // InheritanceHref is null when the server did not advertise acl-inheritance — a repository root (no parent to
    // inherit from) or no CanManagePermissions. The toggle is hidden then rather than offering a certain refusal
    // (#426, ADR 0543).
    public sealed record AclInfo(bool Forbidden, bool BreaksInheritance, List<AclEntryInfo> Entries, List<GrantablePrincipalInfo> Principals, string? InheritanceHref);

    // Reads the document FIRST and works outwards from what it advertises (ADR 0543, issue #416). The order
    // matters: `acl-entries` is gated on CanManagePermissions, so its ABSENCE is the answer the dialog needs —
    // it no longer discovers "you may not manage access" by sending a request designed to be refused with a 403.
    // The collection then hands over `grantable-principals`, so the picker is one link away rather than a second
    // path assembled here. The whole call is best-effort in the same direction it always was: any failure reads
    // as "no rights", which hides affordances rather than offering ones that cannot work.
    public async Task<AclInfo> GetAclAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        JsonElement doc;
        try
        {
            doc = await _http.GetFromJsonAsync<JsonElement>(DocumentAddress(documentId), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new AclInfo(true, false, [], [], null);
        }

        var docLinks = ParseLinks(doc) ?? new Dictionary<string, string>();
        if (!docLinks.TryGetValue("acl-entries", out var aclHref))
        {
            return new AclInfo(true, false, [], [], null);
        }

        var breaksInheritance = doc.TryGetProperty("breaksInheritance", out var bi) && bi.ValueKind == JsonValueKind.True;
        docLinks.TryGetValue("acl-inheritance", out var inheritanceHref);

        using var listResponse = await _http.GetAsync(aclHref, cancellationToken);
        if (listResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            return new AclInfo(true, false, [], [], null);
        }

        listResponse.EnsureSuccessStatusCode();

        var entries = new List<AclEntryInfo>();
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (listJson.TryGetProperty("entries", out var es))
        {
            foreach (var e in es.EnumerateArray())
            {
                entries.Add(new AclEntryInfo(
                    e.GetProperty("principalType").GetString() ?? "",
                    e.GetProperty("principalId").GetGuid(),
                    ReadRights(e),
                    ParseLinks(e)));
            }
        }

        var principals = new List<GrantablePrincipalInfo>();
        var pj = await _http.GetFromJsonAsync<JsonElement>(RequireRel(listJson, "grantable-principals", "The ACL collection"), cancellationToken);
        if (pj.TryGetProperty("principals", out var ps))
        {
            foreach (var p in ps.EnumerateArray())
            {
                principals.Add(new GrantablePrincipalInfo(
                    p.GetProperty("type").GetString() ?? "",
                    p.GetProperty("id").GetGuid(),
                    p.GetProperty("name").GetString() ?? "",
                    ParseLinks(p)));
            }
        }

        return new AclInfo(false, breaksInheritance, entries, principals, inheritanceHref);
    }

    // Writes the rights at the address the ROW gave us for writing them — `grant` on a principal being added,
    // `edit` on an entry already there. One method, because it is one operation: the two rels differ only in
    // which side of the same address the server chose to advertise (ADR 0555).
    public async Task SetAclEntryAsync(IAdvertisesLinks row, AclRights rights, CancellationToken cancellationToken = default)
    {
        var href = row.Href("grant") ?? row.Href("edit")
            ?? throw new InvalidOperationException($"The row '{row.Name}' advertised neither 'grant' nor 'edit' — you may not change its access (ADR 0543/0555).");
        using var response = await _http.PutAsJsonAsync(href, rights, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException(Strings.Get("MaInsufficientRights"));
        }

        await ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    public async Task RevokeAclEntryAsync(AclEntryInfo entry, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(RequireHref(entry, "remove"), cancellationToken);
        await ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    public sealed record EffectiveAccessInfo(string? InheritedFrom, List<EffectiveAccessEntryInfo> Entries);

    public sealed record EffectiveAccessEntryInfo(string Type, Guid Id, string Name, string Access, string? ViaGroup, AclRights Rights);

    // The resolved "who can actually access this" view (ADR 0488): effective grants resolved to people (groups
    // expanded to members, tenant admins flagged).
    // `effective` is a rel on the ACL COLLECTION, so the collection is read first — one hop that also answers
    // "may I see this at all" by whether the document advertised `acl-entries` (ADR 0543).
    public async Task<EffectiveAccessInfo> GetEffectiveAccessAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var collection = await _http.GetFromJsonAsync<JsonElement>(await DocumentRelAsync(documentId, "acl-entries", cancellationToken), cancellationToken);
        var json = await _http.GetFromJsonAsync<JsonElement>(RequireRel(collection, "effective", "The ACL collection"), cancellationToken);

        var entries = new List<EffectiveAccessEntryInfo>();
        if (json.TryGetProperty("entries", out var es))
        {
            foreach (var e in es.EnumerateArray())
            {
                entries.Add(new EffectiveAccessEntryInfo(
                    e.GetProperty("type").GetString() ?? "",
                    e.GetProperty("id").GetGuid(),
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("access").GetString() ?? "",
                    e.TryGetProperty("viaGroup", out var vg) && vg.ValueKind == JsonValueKind.String ? vg.GetString() : null,
                    ReadRights(e)));
            }
        }

        var inheritedFrom = json.TryGetProperty("inheritedFrom", out var inf) && inf.ValueKind == JsonValueKind.String ? inf.GetString() : null;
        return new EffectiveAccessInfo(inheritedFrom, entries);
    }

    // Break (copy inherited grants down) / restore (discard own grants) ACL inheritance (ADR 0486 follow-up).
    // Takes the advertised href rather than composing one (ADR 0543); the caller only has it when the server
    // offered the action.
    public async Task SetInheritanceAsync(string href, bool breaksInheritance, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(href, new { breaksInheritance }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException(Strings.Get("MaInsufficientRights"));
        }

        await ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    private static AclRights ReadRights(JsonElement e) => new(
        e.GetProperty("canSee").GetBoolean(),
        e.GetProperty("canReadContent").GetBoolean(),
        e.GetProperty("canEditContent").GetBoolean(),
        e.GetProperty("canEditIndexData").GetBoolean(),
        e.GetProperty("canCreateSubItems").GetBoolean(),
        e.GetProperty("canDelete").GetBoolean(),
        e.GetProperty("canMove").GetBoolean(),
        e.GetProperty("canAnnotate").GetBoolean(),
        e.GetProperty("canManagePermissions").GetBoolean());

    // ---- Group membership (ADR "Group membership editing") ------------------------------------------

    public Task<List<UserOptionInfo>> GetGroupMembersAsync(PrincipalInfo group, CancellationToken cancellationToken = default) =>
        LoadPagedAsync(RequireHref(group, "members"), "members", ParseMember, cancellationToken);

    // The API takes the member in the BODY of a POST to the collection now, so the group row's `members`
    // address serves every add — the chosen user travels as data, not as a path segment (issue #416).
    public async Task AddGroupMemberAsync(PrincipalInfo group, Guid userId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RequireHref(group, "members"), new { userId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage members.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveGroupMemberAsync(UserOptionInfo member, CancellationToken cancellationToken = default)
    {
        var removeHref = member.RemoveHref
            ?? throw new InvalidOperationException("The member row advertised no 'remove' rel (ADR 0543/0555).");
        using var response = await _http.DeleteAsync(removeHref, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static UserOptionInfo ParseMember(JsonElement e) =>
        new(e.GetProperty("id").GetGuid(),
            e.GetProperty("displayName").GetString() ?? "",
            ParseLinks(e) is { } links && links.TryGetValue("remove", out var removeHref) ? removeHref : null);

    // ---- Profile photo (ADR "User profile photo") ---------------------------------------------------

    public Task SetUserPhotoAsync(PrincipalInfo user, byte[] png, CancellationToken cancellationToken = default) =>
        PutPhotoAsync(RequireHref(user, "photo"), png, cancellationToken);

    public async Task SetMyPhotoAsync(byte[] png, CancellationToken cancellationToken = default) =>
        await PutPhotoAsync(await MeHrefAsync("photo", cancellationToken), png, cancellationToken);

    private async Task PutPhotoAsync(string url, byte[] png, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var response = await _http.PutAsync(url, content, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to change this photo.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("That image could not be used as a profile photo.");
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>The caller's OWN avatar, at the address the `me` resource advertises for it.</summary>
    public async Task<byte[]?> GetMyPhotoAsync(CancellationToken cancellationToken = default) =>
        await GetPhotoAsync(await MeHrefAsync("photo", cancellationToken), cancellationToken);

    // The normalized PNG bytes, or null if the user has no photo.
    public Task<byte[]?> GetUserPhotoAsync(PrincipalInfo user, CancellationToken cancellationToken = default) =>
        GetPhotoAsync(RequireHref(user, "photo"), cancellationToken);

    private async Task<byte[]?> GetPhotoAsync(string photoHref, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(photoHref, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Removes the caller's OWN avatar, at the address the `me` resource advertises.</summary>
    public Task DeleteMyPhotoAsync(CancellationToken cancellationToken = default) =>
        DeletePhotoAsync(MeHrefAsync("photo", cancellationToken), cancellationToken);

    public Task DeleteUserPhotoAsync(PrincipalInfo user, CancellationToken cancellationToken = default) =>
        DeletePhotoAsync(Task.FromResult(RequireHref(user, "photo")), cancellationToken);

    private async Task DeletePhotoAsync(Task<string> photoHref, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(await photoHref, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static PrincipalInfo ParseUser(JsonElement e) => new(
        false,
        e.GetProperty("id").GetGuid(),
        e.GetProperty("displayName").GetString() ?? "",
        !e.TryGetProperty("isActive", out var a) || a.ValueKind == JsonValueKind.True,
        ParseRights(e),
        e.TryGetProperty("mfaEnabled", out var mfa) && mfa.ValueKind == JsonValueKind.True,
        ParseLinks(e));

    private static PrincipalInfo ParseGroup(JsonElement e) => new(
        true,
        e.GetProperty("id").GetGuid(),
        e.GetProperty("name").GetString() ?? "",
        true,
        ParseRights(e),
        false,
        ParseLinks(e));

    private static SystemRightsData ParseRights(JsonElement e)
    {
        if (!e.TryGetProperty("rights", out var r))
        {
            return new SystemRightsData(false, false, false, false, false, false, false, false, false, false, false, false, false);
        }

        bool B(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new SystemRightsData(
            B("isTenantAdmin"), B("canImpersonate"), B("canOverrideCheckout"), B("canLegalHold"),
            B("canManageClassification"), B("canResetMfa"), B("canManageRepositories"), B("canManageMasks"),
            B("canManageServiceAccounts"), B("canManageUsers"), B("canViewAuditLog"), B("canExport"), B("canImport"),
            B("canManageInboxes"), B("canCreateExternalLink"),
            r.TryGetProperty("clearanceRank", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt32() : 0);
    }

    private static string? StrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? FindLink(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links))
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.GetProperty("rel").GetString() == rel)
            {
                return link.GetProperty("href").GetString();
            }
        }

        return null;
    }
}
