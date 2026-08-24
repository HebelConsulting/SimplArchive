using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The documents area's client (#443, the finale): repositories/children, filing, rename/move/delete, the
/// recycle bin, references, tags, ACL, chat, index data, masks and sensitivity — all over the shared
/// authenticated <see cref="ApiCore"/>. Reached as <c>api.Documents</c>.
/// </summary>
/// <remarks>
/// Fully href-based since the #443 endgame: every method takes an address a listing row, a payload or the
/// document resource itself advertised (ADR 0543/0555). The one composed URL the desktop ever had
/// (<c>DocumentAddress</c>) is gone — a caller that holds only an id has nothing to call here, by design.
/// </remarks>
public sealed class DocumentsClient(ApiCore core, Func<RemindersClient> reminders)
{
    private readonly ApiCore _core = core;

    /// <summary>For sibling extension files that carry this client's own wire choreography (the debt-list
    /// pressure valve — see <c>IndexDataWrites</c>).</summary>
    internal ApiCore Core => _core;

    /// <summary>A duplicate-probe hit (ADRs 0398/0686); the probe itself lives in <c>IndexDataWrites</c>.</summary>
    public sealed record DuplicateInfo(Guid Id, string Name, string Path);
    private readonly Func<RemindersClient> _reminders = reminders;

    // The bulk collection's advertised action links (ADR 0557: a structurally fixed rel set may be cached).
    private IReadOnlyDictionary<string, string>? _bulkLinks;
    private readonly SemaphoreSlim _bulkGate = new(1, 1);

    public sealed record GrantablePrincipalInfo(string Type, Guid Id, string Name,
        IReadOnlyDictionary<string, string>? Links = null) : IAdvertisesLinks
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    public sealed record ChatThread(List<Comment> Messages, string? MentionableUsersHref);

    /// <summary>What is already in the target folder under a dropped file's name, and a free name to offer instead.</summary>
    /// <param name="existing">The row whose name collided, or null if it went away between the 409 and this read.
    /// It carries its own addresses, so filing a new version of it follows the row's <c>versions</c> rel.</param>
    /// <param name="suggestedName">A stem not currently taken here — "Invoice" becomes "Invoice (2)".</param>
    public sealed record NameConflict(Node? Existing, string SuggestedName);

    // Everything the Manage-access dialog needs in one load. Forbidden = the caller lacks CanManagePermissions
    // (the list/picker endpoints 403), so the dialog shows a read-only message instead of a broken editor.
    // InheritanceHref is null when the server did not advertise acl-inheritance — a repository root (no parent to
    // inherit from) or no CanManagePermissions. The toggle is hidden then rather than offering a certain refusal
    // (#426, ADR 0543).
    public sealed record AclInfo(bool Forbidden, bool BreaksInheritance, List<AclEntryInfo> Entries, List<GrantablePrincipalInfo> Principals, string? InheritanceHref);

    // A folder that references a given item, with its full display path — see ADR "References-of-an-item list".
    // OpenHref is the row's own `open` address (ADR 0555) — null where the server withheld it.
    public sealed record ReferencingFolder(Guid Id, string Name, string Path, string? OpenHref = null);

    // The references-of-an-item view: the document's real primary location (null when it's a repository root or
    // the caller can't see the parent) plus the folders that reference it (ADR 0506).
    public sealed record ReferencesView(ReferencingFolder? Primary, IReadOnlyList<ReferencingFolder> Folders);

    public sealed record IndexField(string FieldName, IReadOnlyList<string> Values);

    public sealed record MentionableUser(Guid Id, string DisplayName);

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

    // A server-intray item — a staged file (ADR "S3-backed inbox"). Download is a presigned URL; HasMask tells
    // whether a `{name}.mask.json` staging sidecar exists (ADR "Inbox item classification + preview"). Group/User
    // label a non-own item's source queue (ADR 0532); MoveUrl is its move action, source query already baked in.
    // Links are the addresses the listing advertised for THIS item — preview, mask, file, move and its own
    // deletion — each already carrying the right source prefix for a group or another user's intray, which is
    // exactly the part the client used to rebuild by hand (ADR 0543/0555, issue #416).
    // A reference (shortcut) filed in a folder — see ADR "Desktop drag-and-drop move and reference".
    // TargetId/Name/HasVersions/HasSubfolders describe the referenced item; ReferenceId identifies the
    // shortcut row (for delete); RealParentId is the target's real home folder (for "Go to …").
    // DeleteHref is the shortcut row's own `delete` address (ADR 0543) — the pair of ids that used to rebuild
    // it are still here because the tree needs them, but nothing composes a URL out of them any more.
    public sealed record Reference(
        Guid ReferenceId, Guid TargetId, string Name, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, Guid? RealParentId,
        string? DeleteHref = null, IReadOnlyDictionary<string, string>? Links = null);

    public async Task<List<Node>> GetRepositoriesAsync(CancellationToken cancellationToken = default) =>
        await _core.LoadPagedAsync(await _core.RootHrefAsync("repositories", cancellationToken), "repositories", ParseNode, cancellationToken);

    // Takes the advertised href (node.Href("children")), not a folder id (ADR 0543, issue #416). Every listing
    // that can produce a row here advertises it — the children listing and the repositories listing both do.
    public Task<List<Node>> GetChildrenAsync(string childrenHref, CancellationToken cancellationToken = default) =>
        _core.LoadPagedAsync(childrenHref, "children", ParseNode, cancellationToken);

    // The item's ancestor folder ids, repository-root first down to its immediate parent (issue #340) — used to
    // reveal a search hit in the lazy tree. Empty for an item filed at a repository root.
    public async Task<List<Guid>> GetAncestorsAsync(string ancestorsHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(ancestorsHref, cancellationToken);
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
    // The order travels IN the children envelope, so a screen that is listing the folder anyway should call
    // GetFolderContentsAsync and read both from one response. This overload is for the callers that want only
    // the number (a VM check), and it asks for a single row rather than a page to get it.
    public async Task<int> GetContentsSortOrderAsync(string childrenHref, CancellationToken cancellationToken = default) =>
        ReadContentsSortOrder(await _core.Http.GetFromJsonAsync<JsonElement>(childrenHref + "?limit=1", cancellationToken));

    // Sets the folder's persisted default contents sort order (CanEditIndexData-gated).
    public async Task SetContentsSortOrderAsync(string contentsSortOrderHref, int sortOrder, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(contentsSortOrderHref, new { sortOrder }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the contents sort order ({(int)response.StatusCode}).");
        }
    }

    // Lists a .zip document's entries on demand (ADR "Zip file browsing") — nothing is unpacked.
    public async Task<IReadOnlyList<ArchiveEntryInfo>> GetArchiveEntriesAsync(string archiveEntriesHref, CancellationToken cancellationToken = default)
    {
        // The rel is advertised only for a zip, so its PRESENCE is the server answering "can I browse inside
        // this?" instead of the client comparing ".zip" (#416).
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(archiveEntriesHref, cancellationToken);
        var entries = new List<ArchiveEntryInfo>();
        if (json.TryGetProperty("entries", out var array))
        {
            foreach (var e in array.EnumerateArray())
            {
                entries.Add(new ArchiveEntryInfo(
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("path").GetString() ?? "",
                    e.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    ApiCore.RelHref(e, "download")));
            }
        }

        return entries;
    }

    public async Task<List<IndexField>> GetIndexDataAsync(string indexDataHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(indexDataHref, cancellationToken);
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

    // Data-classification / sensitivity label (ADR "Configurable sensitivity labels + upload defaults") — the
    // per-tenant label on the document (id/name/colour + whether it watermarks), read from the document resource.
    public sealed record DocumentSensitivityInfo(Guid? LabelId, string Name, string? Color, bool Watermark);

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

    public async Task<DocumentDetailInfo> GetDocumentDetailAsync(string documentSelfHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(documentSelfHref, cancellationToken);

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
            ApiCore.RelHref(json, "external-links"),
            json.TryGetProperty("contentsSortOrder", out var so) && so.ValueKind == JsonValueKind.Number ? so.GetInt32() : 0,
            ApiCore.ParseLinks(json));
    }

    public async Task SetSensitivityAsync(string sensitivityHref, Guid? labelId, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(sensitivityHref, new { labelId }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the sensitivity label ({(int)response.StatusCode}).");
        }
    }

    // Free-form tags (ADR "Document tags"). GET the document's tags; PUT-replaces the whole set (the server
    // normalizes/dedupes and returns the stored set); the tenant tag catalog backs add-box autocomplete.
    // Takes the advertised href (detail.Href("tags")), not a document id (ADR 0543, issue #416).
    public async Task<IReadOnlyList<string>> GetTagsAsync(string tagsHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(tagsHref, cancellationToken);
        return ReadTags(json);
    }

    public async Task<IReadOnlyList<string>> GetTagCatalogAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("tags", cancellationToken), cancellationToken);
        return ReadTags(json);
    }

    public async Task<BulkResult> BulkMoveAsync(IEnumerable<Guid> ids, Guid parentId, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("move", cancellationToken), new { ids = ids.ToArray(), parentId }, cancellationToken);

    public async Task<BulkResult> BulkDeleteAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("delete", cancellationToken), new { ids = ids.ToArray() }, cancellationToken);

    public async Task<BulkResult> BulkSetSensitivityAsync(IEnumerable<Guid> ids, Guid? labelId, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("sensitivity", cancellationToken), new { ids = ids.ToArray(), labelId }, cancellationToken);

    // The latest confirmed version's workflow (null if the document has no confirmed version).
    public async Task<WorkflowClient.WorkflowInfo?> GetWorkflowAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
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

        if (latest is not { } cur || ApiCore.FindLink(cur, "workflow") is not { } wfLink)
        {
            return null;
        }

        var json = await _core.Http.GetFromJsonAsync<JsonElement>(wfLink.TrimStart('/'), cancellationToken);
        var links = new Dictionary<string, string>();
        if (json.TryGetProperty("links", out var ls))
        {
            foreach (var l in ls.EnumerateArray())
            {
                links[l.GetProperty("rel").GetString() ?? ""] = l.GetProperty("href").GetString() ?? "";
            }
        }

        var history = new List<WorkflowClient.WorkflowTransitionInfo>();
        if (json.TryGetProperty("history", out var hs))
        {
            foreach (var h in hs.EnumerateArray())
            {
                history.Add(new WorkflowClient.WorkflowTransitionInfo(
                    h.GetProperty("toStatusName").GetString() ?? "",
                    SimplArchiveApiClient.StrOrNull(h, "assignedToName"), SimplArchiveApiClient.StrOrNull(h, "performedByName"), SimplArchiveApiClient.StrOrNull(h, "rejectionReason")));
            }
        }

        return new WorkflowClient.WorkflowInfo(
            json.GetProperty("status").GetInt32(),
            json.GetProperty("statusName").GetString() ?? "",
            SimplArchiveApiClient.StrOrNull(json, "assignedToName"), history, links);
    }

    public async Task<bool> GetSubscriptionAsync(string subscriptionHref, CancellationToken cancellationToken = default) =>
        await _reminders().GetSubscriptionAsync(subscriptionHref, cancellationToken);

    public async Task SetSubscriptionAsync(string subscriptionHref, bool subscribe, CancellationToken cancellationToken = default) =>
        await _reminders().SetSubscriptionAsync(subscriptionHref, subscribe, cancellationToken);

    public async Task<IReadOnlyList<RemindersClient.ReminderInfo>> GetRemindersAsync(string remindersHref, CancellationToken cancellationToken = default) =>
        (await GetRemindersViewAsync(remindersHref, cancellationToken)).Reminders;

    /// <summary>
    /// The document's reminders AND the address of its target picker, from ONE read of the collection that
    /// advertises both. The Remind… dialog wants the two together; asking for them separately would mean
    /// fetching the document twice and the collection twice, which is how following rels turns into four
    /// requests where there used to be two (ADR 0543, issue #416).
    /// </summary>
    public async Task<(IReadOnlyList<RemindersClient.ReminderInfo> Reminders, string TargetsHref)> GetRemindersViewAsync(string remindersHref, CancellationToken cancellationToken = default)
    {
        var collection = await _core.Http.GetFromJsonAsync<JsonElement>(remindersHref, cancellationToken);
        return (RemindersClient.ParseReminders(collection), ApiCore.RequireRel(collection, "targets", "The reminders collection"));
    }

    public async Task CreateReminderAsync(string remindersHref, DateTimeOffset remindAt, string? note, int recurrence, Guid? targetUserId, CancellationToken cancellationToken = default) =>
        await _reminders().CreateReminderAsync(remindersHref, remindAt, note, recurrence, targetUserId, cancellationToken);

    // The thread AND the rel that reaches its mention picker, from one request. The href has to travel with the
    // messages: it is advertised on the list resource, and re-fetching it separately would mean composing the
    // thread's URL a second time, which is exactly what ADR 0543 forbids.
    public async Task<ChatThread> GetChatAsync(string chatHref, CancellationToken cancellationToken = default)
    {
        string? mentionableUsersHref = null;
        var messages = await _core.LoadPagedAsync(chatHref, "messages", ParseComment, cancellationToken,
            // First page only: the rel describes the thread, not the page.
            page => mentionableUsersHref ??= ApiCore.FindLink(page, "mentionable-users"));

        return new ChatThread(messages, mentionableUsersHref);
    }

    // Who may be @-mentioned on this document. The server filters by who can SEE it — mentioning somebody
    // subscribes them and sends a notification carrying the document's name, so this is not a staff directory
    // (issue #383). The href comes from the thread's "mentionable-users" rel; the client never builds it.
    public async Task<IReadOnlyList<MentionableUser>> GetMentionableUsersAsync(string href, string query, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.GetAsync($"{href}?q={Uri.EscapeDataString(query)}", cancellationToken);
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

    // Creates a folder = a child Document with no version (ADR 0175). Duplicate name -> 409, no permission -> 403.
    // maskId is the entry's own value going back unread (ADR 0543) — it is the only thing a tenant-authored
    // mask has, since slugs exist for the handful of kinds that shipped with one. Null keeps the old body
    // exactly, so the self-tests and every other caller that just wants a plain folder are unchanged.
    public Task CreateFolderAsync(string childrenHref, string name, Guid? maskId = null, CancellationToken cancellationToken = default) =>
        PostCreateAsync(childrenHref, maskId is { } id ? new { name, maskId = id } : (object)new { name }, name, "folder", cancellationToken);

    // A section, and a note, inside a notebook (#564). Each is its own sub-resource rather than a folderMask on
    // POST children, and the caller reaches it by following a rel the server advertised — so which folders can
    // hold which stays a server rule, never a mask name the client had to know.
    public Task CreateSectionAsync(string sectionsHref, string name, CancellationToken cancellationToken = default) =>
        PostCreateAsync(sectionsHref, new { name }, name, "section", cancellationToken);

    public Task CreateNoteAsync(string notesHref, string title, string body, CancellationToken cancellationToken = default) =>
        PostCreateAsync(notesHref, new { title, body }, title, "note", cancellationToken);

    // One body for all three: they differ only in the address, the payload and the noun in the refusal.
    private async Task PostCreateAsync(
        string href, object payload, string name, string what, CancellationToken cancellationToken)
    {
        using var response = await _core.Http.PostAsJsonAsync(href, payload, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A folder or document named '{name}' already exists here.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException($"You don't have permission to create a {what} here.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task CreateRepositoryAsync(string name, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("repositories", cancellationToken), new { name }, cancellationToken);
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
    public async Task RenameAsync(string documentSelfHref, string newName, CancellationToken cancellationToken = default)
    {
        var etag = await GetETagAsync(documentSelfHref, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, documentSelfHref)
        {
            Content = JsonContent.Create(new { name = newName }),
        };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _core.Http.SendAsync(request, cancellationToken);
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
    public async Task DeleteAsync(string documentSelfHref, CancellationToken cancellationToken = default)
    {
        var etag = await GetETagAsync(documentSelfHref, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Delete, documentSelfHref);
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _core.Http.SendAsync(request, cancellationToken);
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
        using var response = await _core.Http.PostAsync(ApiCore.RequireHref(entry, "restore"), null, cancellationToken);
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
        _core.LoadPagedAsync(
            repository.Href("recycle-bin") ?? throw new InvalidOperationException($"The repository '{repository.Name}' advertised no 'recycle-bin' rel (ADR 0543/0555)."),
            "items", ParseRecycleBinItem, cancellationToken);

    // Permanently purges a recycle-bin item + its subtree (ADR "Manual hard-delete / purge") — tenant-admin only.
    public async Task PurgeAsync(IAdvertisesLinks entry, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(ApiCore.RequireHref(entry, "purge"), null, cancellationToken);
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

    // The references (shortcuts) filed in a folder — see ADR "Desktop drag-and-drop move and reference".
    public Task<List<Reference>> GetReferencesAsync(string referencesHref, CancellationToken cancellationToken = default) =>
        _core.LoadPagedAsync(referencesHref, "references", ParseReference, cancellationToken);

    // The folders that reference a given item (with full paths) — see ADR "References-of-an-item list".
    public async Task<List<ReferencingFolder>> GetReferencingFoldersAsync(string referencingFoldersHref, CancellationToken cancellationToken = default) =>
        await _core.LoadPagedAsync(referencingFoldersHref, "folders", ParseReferencingFolder, cancellationToken);

    // The full references view — the item's real primary location plus every referencing folder (ADR 0506). The
    // primary location is a top-level object on the first page (not part of the paged array), so this can't reuse
    // LoadPagedAsync; it walks the pages itself.
    public async Task<ReferencesView> GetReferencesViewAsync(string referencingFoldersHref, CancellationToken cancellationToken = default)
    {
        var folders = new List<ReferencingFolder>();
        ReferencingFolder? primary = null;
        string? next = referencingFoldersHref;
        var first = true;

        while (next is not null)
        {
            var page = await _core.Http.GetFromJsonAsync<JsonElement>(next, cancellationToken);
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

            next = ApiCore.FindLink(page, "next");
        }

        return new ReferencesView(primary, folders);
    }

    // Promotes a referenced folder to be the document's primary location (ADR 0506): atomic move + leave a
    // reference at the former home. Same If-Match contract as MoveAsync.
    public async Task SetPrimaryLocationAsync(string documentSelfHref, Guid folderId, CancellationToken cancellationToken = default)
    {
        var (links, etag) = await GetLinksAndETagAsync(documentSelfHref, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, links.TryGetValue("set-primary-location", out var h) ? h : throw new InvalidOperationException("The document advertised no 'set-primary-location' rel (ADR 0543)."))
        {
            Content = JsonContent.Create(new { folderId }),
        };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _core.Http.SendAsync(request, cancellationToken);
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

    // Moves (reparents) an item into another folder. Requires If-Match (like rename/delete), fetched via a
    // HEAD. 400 = into its own subtree, 403 = no permission (CanMove/CanCreateSubItems), 409 = name clash.
    public async Task MoveAsync(string documentSelfHref, Guid newParentId, CancellationToken cancellationToken = default)
    {
        var (links, etag) = await GetLinksAndETagAsync(documentSelfHref, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, links.TryGetValue("move", out var h) ? h : throw new InvalidOperationException("The document advertised no 'move' rel (ADR 0543)."))
        {
            Content = JsonContent.Create(new { parentId = newParentId }),
        };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await _core.Http.SendAsync(request, cancellationToken);
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

    public async Task CreateReferenceAsync(string referencesHref, Guid targetId, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(referencesHref, new { targetId }, cancellationToken);
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
        using var response = await _core.Http.DeleteAsync(deleteHref, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to remove this reference.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Uploads a file into a folder, mirroring the web client's drag-drop flow (ADR 0216): create the child
    // Document, create a Pending version, PUT the bytes straight to the presigned URL (never proxied), then
    // finalise (server hashes + assigns the version number). The server assigns the mask at finalize (eMail
    // for .eml/.msg, else Basic Entry — ADR "Email auto-classification"), so the client doesn't classify.
    // Returns the created document's id. An optional feed comment is posted on it after finalize (ADR "Filing
    // posts a feed comment") — used by list-pane drop filing into a folder (ADR "List-pane drop filing").
    public async Task<Guid> UploadFileAsync(string childrenHref, string fileName, byte[] bytes, string? comment = null, CancellationToken cancellationToken = default)
    {
        // Document.Name is the stem (no extension); the extension rides on the version's object key (ADR
        // "Extension off Document.Name, derived from the object key").
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        using var createResponse = await _core.Http.PostAsJsonAsync(childrenHref, new { name }, cancellationToken);
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
        using var versionResponse = await _core.Http.PostAsJsonAsync(ApiCore.RequireRel(created, "versions", "The created document"), new { fileExtension = extension, comment = versionComment }, cancellationToken);
        versionResponse.EnsureSuccessStatusCode();
        var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var uploadUrl = version.GetProperty("uploadUrl").GetString()!;

        using var uploadContent = new ByteArrayContent(bytes);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(fileName));
        using var uploadResponse = await ApiCore.Anonymous.PutAsync(uploadUrl, uploadContent, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();

        // Finalize is a PUT to the version's OWN address, which the create response just advertised as `self`.
        using var finalizeResponse = await _core.Http.PutAsync(ApiCore.RequireRel(version, "self", "The pending version"), null, cancellationToken);
        finalizeResponse.EnsureSuccessStatusCode();

        // The server assigns the mask at finalize (eMail for .eml/.msg, else Basic Entry) — ADR "Email
        // auto-classification"; the client no longer classifies.

        return documentId;
    }

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

    internal static Node ParseNode(JsonElement item) => new(
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
        ApiCore.ParseLinks(item),
        ParseAdmits(item),
        item.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null);

    // What this folder will accept, with the address for each (#673). An absent or empty array means the
    // client offers no creates here — the same reading as a missing rel: not available to you, here, now.
    private static IReadOnlyList<CreatableChild> ParseAdmits(JsonElement item) =>
        item.TryGetProperty("admits", out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(e => new CreatableChild(
                e.GetProperty("maskId").GetGuid(),
                e.GetProperty("name").GetString() ?? "",
                e.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.True,
                e.GetProperty("href").GetString() ?? "",
                e.TryGetProperty("folderMask", out var fm) && fm.ValueKind == JsonValueKind.String ? fm.GetString() : null,
                e.TryGetProperty("prompt", out var pr) ? pr.GetString() ?? "name" : "name",
                e.TryGetProperty("icon", out var ei) && ei.ValueKind == JsonValueKind.String ? ei.GetString() : null))]
            : [];

    // Reads the document FIRST and works outwards from what it advertises (ADR 0543, issue #416). The order
    // matters: `acl-entries` is gated on CanManagePermissions, so its ABSENCE is the answer the dialog needs —
    // it no longer discovers "you may not manage access" by sending a request designed to be refused with a 403.
    // The collection then hands over `grantable-principals`, so the picker is one link away rather than a second
    // path assembled here. The whole call is best-effort in the same direction it always was: any failure reads
    // as "no rights", which hides affordances rather than offering ones that cannot work.
    public async Task<AclInfo> GetAclAsync(string documentSelfHref, CancellationToken cancellationToken = default)
    {
        JsonElement doc;
        try
        {
            doc = await _core.Http.GetFromJsonAsync<JsonElement>(documentSelfHref, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new AclInfo(true, false, [], [], null);
        }

        var docLinks = ApiCore.ParseLinks(doc) ?? new Dictionary<string, string>();
        if (!docLinks.TryGetValue("acl-entries", out var aclHref))
        {
            return new AclInfo(true, false, [], [], null);
        }

        var breaksInheritance = doc.TryGetProperty("breaksInheritance", out var bi) && bi.ValueKind == JsonValueKind.True;
        docLinks.TryGetValue("acl-inheritance", out var inheritanceHref);

        using var listResponse = await _core.Http.GetAsync(aclHref, cancellationToken);
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
                    ApiCore.ParseLinks(e)));
            }
        }

        var principals = new List<GrantablePrincipalInfo>();
        var pj = await _core.Http.GetFromJsonAsync<JsonElement>(ApiCore.RequireRel(listJson, "grantable-principals", "The ACL collection"), cancellationToken);
        if (pj.TryGetProperty("principals", out var ps))
        {
            foreach (var p in ps.EnumerateArray())
            {
                principals.Add(new GrantablePrincipalInfo(
                    p.GetProperty("type").GetString() ?? "",
                    p.GetProperty("id").GetGuid(),
                    p.GetProperty("name").GetString() ?? "",
                    ApiCore.ParseLinks(p)));
            }
        }

        return new AclInfo(false, breaksInheritance, entries, principals, inheritanceHref);
    }

    public sealed record EffectiveAccessInfo(string? InheritedFrom, List<EffectiveAccessEntryInfo> Entries);

    public sealed record EffectiveAccessEntryInfo(string Type, Guid Id, string Name, string Access, string? ViaGroup, AclRights Rights);

    // The resolved "who can actually access this" view (ADR 0488): effective grants resolved to people (groups
    // expanded to members, tenant admins flagged).
    // `effective` is a rel on the ACL COLLECTION, so the collection is read first — one hop that also answers
    // "may I see this at all" by whether the document advertised `acl-entries` (ADR 0543).
    public async Task<EffectiveAccessInfo> GetEffectiveAccessAsync(string aclEntriesHref, CancellationToken cancellationToken = default)
    {
        var collection = await _core.Http.GetFromJsonAsync<JsonElement>(aclEntriesHref, cancellationToken);
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(ApiCore.RequireRel(collection, "effective", "The ACL collection"), cancellationToken);

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
        using var response = await _core.Http.PutAsJsonAsync(href, new { breaksInheritance }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException(Strings.Get("MaInsufficientRights"));
        }

        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    private static ReferencingFolder ParseReferencingFolder(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
        // The row's own `open` address (ADR 0555) — how "Go to"/promote-then-navigate reaches the folder.
        ApiCore.RelHref(item, "open"));

    private static Reference ParseReference(JsonElement item) => new(
        item.GetProperty("referenceId").GetGuid(),
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
        item.TryGetProperty("hasVersions", out var hv) && hv.GetBoolean(),
        item.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
        item.TryGetProperty("hasReferences", out var hr) && hr.GetBoolean(),
        item.TryGetProperty("realParentId", out var rp) && rp.ValueKind != JsonValueKind.Null ? rp.GetGuid() : null,
        ApiCore.RelHref(item, "delete"),
        // A reference row stands for a REAL document, and the server advertises the same target sub-resources
        // a children row gets (#416) — carry them so the row is not quietly less capable than its neighbour.
        ApiCore.ParseLinks(item));

    private static RecycleBinItem ParseRecycleBinItem(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.GetProperty("deletedAt").GetDateTimeOffset(),
        ApiCore.ParseLinks(item));

    private static Comment ParseComment(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.TryGetProperty("parentMessageId", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetGuid() : null,
        item.GetProperty("body").GetString() ?? "",
        item.TryGetProperty("authorName", out var a) ? a.GetString() ?? "" : "",
        item.GetProperty("createdAt").GetDateTimeOffset(),
        ApiCore.RelHref(item, "author-card"),
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

    internal static int ReadContentsSortOrder(JsonElement envelope) =>
        envelope.TryGetProperty("contentsSortOrder", out var so) && so.ValueKind == JsonValueKind.Number ? so.GetInt32() : 1;

    private static IReadOnlyList<string> ReadTags(JsonElement json) =>
        json.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];

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
                _bulkLinks ??= ApiCore.ParseLinks(await _core.Http.GetFromJsonAsync<JsonElement>(
                    await _core.RootHrefAsync("documentsBulk", cancellationToken), cancellationToken))
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

    private async Task<BulkResult> PostBulkAsync(string url, object body, CancellationToken cancellationToken)
    {
        var response = await _core.Http.PostAsJsonAsync(url, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"The bulk action failed ({(int)response.StatusCode}).");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new BulkResult(
            json.TryGetProperty("succeeded", out var s) ? s.GetInt32() : 0,
            json.TryGetProperty("skipped", out var k) ? k.GetInt32() : 0);
    }

    // Reads the current ETag (a HEAD, cheaper than GET) so a rename/delete can send it as If-Match.
    private async Task<EntityTagHeaderValue?> GetETagAsync(string documentSelfHref, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, documentSelfHref);
        using var response = await _core.Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag;
    }

    // One GET of the document resource for the writes that need BOTH a rel to follow and an If-Match: the
    // links come from the body and the ETag from the same response's headers, so following the rel costs one
    // request instead of a HEAD plus a fetch (ADR 0557).
    private async Task<(IReadOnlyDictionary<string, string> Links, EntityTagHeaderValue? ETag)> GetLinksAndETagAsync(string documentSelfHref, CancellationToken cancellationToken)
    {
        using var response = await _core.Http.GetAsync(documentSelfHref, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var links = ApiCore.ParseLinks(json)
            ?? throw new InvalidOperationException($"'{documentSelfHref}' advertised no links at all (ADR 0543).");
        return (links, response.Headers.ETag);
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

    public sealed record MaskInfo(Guid? MaskId, string? Name, int? VersionNumber, string? DefinitionHref = null); // DefinitionHref: this mask's field definitions, which the catalogue never carries for a typed folder (#729, ADR 0688)

    // System-field values shown always (separate from the mask, ADR "System fields + OCR-language mask
    // field"). Created/CreatedBy/DocumentDate are the currently-shown version's; the OCR-language override +
    // TIFF-source come from the latest TIFF version.
    // DocumentDateHref is the current version's own `document-date` address — the detail pane's Save follows it
    // instead of rebuilding a path out of the two ids beside it (ADR 0543, issue #416).
    public sealed record SystemFields(
        Guid CurrentVersionId, int CurrentVersionNumber, DateTimeOffset CreatedAt, string CreatedByName, string DocumentDate,
        bool HasTiffVersion, string? OcrLanguages, string FileExtension, string? DocumentDateHref = null, string? WorkflowStatus = null);

    public sealed record RepositoryExportOptions(bool ActiveOnly, DateOnly? DocumentDateFrom, DateOnly? DocumentDateTo, DateTimeOffset? FiledFrom, DateTimeOffset? FiledTo, string? CreatedBy, bool IncludePermissions = false);

    public sealed record ImportResultInfo(Guid RootId, string RootName, int Documents, int Versions, int Skipped);

    // Exports a repository/folder + subtree to a .zip (ADR "Repository export"). Tenant-admin-only server-side.
    public async Task<byte[]> ExportRepositoryAsync(string exportHref, RepositoryExportOptions options, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"versions={(options.ActiveOnly ? "active" : "all")}" };
        if (options.DocumentDateFrom is { } df) query.Add($"documentDateFrom={df:yyyy-MM-dd}");
        if (options.DocumentDateTo is { } dt) query.Add($"documentDateTo={dt:yyyy-MM-dd}");
        if (options.FiledFrom is { } ff) query.Add($"filedFrom={Uri.EscapeDataString(ff.UtcDateTime.ToString("o"))}");
        if (options.FiledTo is { } ft) query.Add($"filedTo={Uri.EscapeDataString(ft.UtcDateTime.ToString("o"))}");
        if (!string.IsNullOrWhiteSpace(options.CreatedBy)) query.Add($"createdBy={Uri.EscapeDataString(options.CreatedBy.Trim())}");
        if (options.IncludePermissions) query.Add("includePermissions=true");

        return await _core.Http.GetByteArrayAsync(exportHref + "?" + string.Join("&", query), cancellationToken);
    }

    // Imports an export archive (ADR "Repository import"). targetFolderId == null → a new repository; otherwise
    // grafted under that folder. Tenant-admin-only server-side. Returns the imported root's name + counts.
    public async Task<ImportResultInfo> ImportRepositoryAsync(string? importHref, byte[] zip, bool updateExisting = false, bool includePermissions = false, bool merge = false, string leafConflict = "rename", CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "import.zip");

        // Into a folder → the folder's own `import` rel; a brand-new repository → the one the repositories
        // COLLECTION advertises, since the archive's root becomes a sibling of everything in it and belongs to
        // no repository in particular. `?limit=1` so learning one address doesn't drag back a page of
        // ACL-filtered repositories (ADR 0543, issue #416).
        var basePath = importHref
            ?? ApiCore.RequireRel(
                await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("repositories", cancellationToken) + "?limit=1", cancellationToken),
                "import",
                "The repositories collection");
        var url = $"{basePath}?updateExisting={(updateExisting ? "true" : "false")}&includePermissions={(includePermissions ? "true" : "false")}&merge={(merge ? "true" : "false")}&leafConflict={leafConflict}";
        var response = await _core.Http.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new ImportResultInfo(
            json.GetProperty("rootId").GetGuid(),
            json.GetProperty("rootName").GetString() ?? "",
            json.GetProperty("documents").GetInt32(),
            json.GetProperty("versions").GetInt32(),
            json.GetProperty("skipped").GetInt32());
    }

    public async Task<MaskInfo> GetMaskAsync(string maskHref, CancellationToken cancellationToken = default)
    {
        var mask = await _core.Http.GetFromJsonAsync<JsonElement>(maskHref, cancellationToken);
        return new MaskInfo(
            mask.TryGetProperty("maskId", out var mid) && mid.ValueKind == JsonValueKind.String ? mid.GetGuid() : null,
            mask.TryGetProperty("name", out var n) ? n.GetString() : null,
            mask.TryGetProperty("versionNumber", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null, ApiCore.RelHref(mask, "definition"));
    }

    public async Task<SystemFields?> GetSystemFieldsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        if (VersionsClient.PickCurrentVersionElement(response) is not { } picked)
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
            ApiCore.RelHref(cur, "document-date"), SimplArchiveApiClient.StrOrNull(cur, "workflowStatus"));
    }

    // Sets the document's OCR-language override (ordered codes) and re-runs the searchable-PDF conversion.
    public async Task SetOcrLanguagesAsync(string ocrLanguagesHref, IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(ocrLanguagesHref, new { languages = codes }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set OCR languages ({(int)response.StatusCode}).");
        }
    }

    // Same advertised href as the GET — the tags resource is one address, read or replaced (ADR 0543, #416).
    public async Task<IReadOnlyList<string>> SetTagsAsync(string tagsHref, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(tagsHref, new { tags = tags.ToArray() }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set tags ({(int)response.StatusCode}).");
        }

        return ReadTags(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    // ---- Tag catalog admin (ADR "Tag controlled vocabulary") ----------------------------------------
    // The catalog lists LIVE tags, each advertising self (rename/recolour), retire and merge (issue #416).
    public sealed record TagCatalogItem(Guid Id, string Name, string? Color,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    public async Task UpdateTagAsync(TagCatalogItem tag, string? name, string? color, CancellationToken cancellationToken = default)
    {
        var resp = await _core.Http.PutAsJsonAsync(RequireHref(tag, "self"), new { name, color }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await SimplArchiveApiClient.ErrorMessageAsync(resp, "Could not update the tag."));
    }

    public async Task RetireTagAsync(TagCatalogItem tag, CancellationToken cancellationToken = default) =>
        (await _core.Http.DeleteAsync(RequireHref(tag, "retire"), cancellationToken)).EnsureSuccessStatusCode();

    /// <summary>Merges one tag into another, following the source row's own `merge` rel.</summary>
    public async Task MergeTagAsync(TagCatalogItem tag, Guid intoId, CancellationToken cancellationToken = default)
    {
        var resp = await _core.Http.PostAsJsonAsync(RequireHref(tag, "merge"), new { intoId }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await SimplArchiveApiClient.ErrorMessageAsync(resp, "Could not merge the tags."));
    }

    public async Task<BulkResult> BulkReferenceAsync(IEnumerable<Guid> ids, Guid parentId, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("reference", cancellationToken), new { ids = ids.ToArray(), parentId }, cancellationToken);

    public async Task<BulkResult> BulkAddTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags, CancellationToken cancellationToken = default) =>
        await PostBulkAsync(await BulkRelAsync("tags", cancellationToken), new { ids = ids.ToArray(), tags = tags.ToArray() }, cancellationToken);

    // The latest confirmed version's preview + download links plus whether the preview is a converted rendition.
    public async Task<Preview> GetPreviewAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        // The current version honoring the server's currentVersionId pointer (issue #265), else the latest confirmed.
        if (VersionsClient.PickCurrentVersionElement(response) is not { } picked)
        {
            return new Preview(null, false, null, null, null, "");
        }

        var confirmed = picked.Version;

        var converted = confirmed.TryGetProperty("previewConverted", out var pc) && pc.GetBoolean();
        var extension = confirmed.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "";
        return new Preview(ApiCore.FindLink(confirmed, "preview"), converted, ApiCore.FindLink(confirmed, "download"), ApiCore.FindLink(confirmed, "text-layout"), ApiCore.FindLink(confirmed, "preview-pages"), extension, ApiCore.FindLink(confirmed, "annotations"));
    }

    public async Task PostCommentAsync(string chatHref, string body, Guid? parentCommentId, CancellationToken cancellationToken = default)
    {
        var payload = parentCommentId is { } parent
            ? new { body, parentMessageId = parent }
            : (object)new { body };
        using var response = await _core.Http.PostAsJsonAsync(chatHref, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Empties a repository's recycle bin — permanently purges every item in it (ADR "Manual hard-delete / purge").
    public async Task EmptyRecycleBinAsync(Node repository, CancellationToken cancellationToken = default)
    {
        var bin = await _core.Http.GetFromJsonAsync<JsonElement>(
            repository.Href("recycle-bin") ?? throw new InvalidOperationException($"The repository '{repository.Name}' advertised no 'recycle-bin' rel (ADR 0543/0555)."),
            cancellationToken);
        using var response = await _core.Http.PostAsync(ApiCore.RequireRel(bin, "purge-all", $"The recycle bin of '{repository.Name}'"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can empty the recycle bin.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Uploads bytes as a NEW version of an existing document (the check-in upload) — POST /versions → PUT bytes
    // → finalize. Distinct from UploadFileAsync, which creates a new document.
    public async Task UploadNewVersionAsync(string versionsHref, byte[] bytes, string fileExtension, string? comment = null, CancellationToken cancellationToken = default)
    {
        // The check-in comment is the new version's "why this revision" note (ADR 0528) — set on the version
        // itself, not posted to the chat feed as it used to be.
        var versionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        using var versionResponse = await _core.Http.PostAsJsonAsync(versionsHref, new { fileExtension, comment = versionComment }, cancellationToken);
        if (versionResponse.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("This document is checked out by another user or under a legal hold.");
        }

        versionResponse.EnsureSuccessStatusCode();
        var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var uploadUrl = version.GetProperty("uploadUrl").GetString()!;

        using var uploadContent = new ByteArrayContent(bytes);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType($"x{fileExtension}"));
        using var uploadResponse = await ApiCore.Anonymous.PutAsync(uploadUrl, uploadContent, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();

        using var finalizeResponse = await _core.Http.PutAsync(ApiCore.RequireRel(version, "self", "The pending version"), null, cancellationToken);
        finalizeResponse.EnsureSuccessStatusCode();
    }

    // The candidate reviewers for submitting a document into the workflow (ADR "Workflow assignable-reviewers
    // endpoint") — a light per-document catalog any editor can read, no CanManageUsers needed. Returns empty on
    // no access (e.g. the caller lacks CanEditContent).
    public async Task<IReadOnlyList<UserOptionInfo>> GetAssignableReviewersAsync(string assignableReviewersHref, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _core.Http.GetFromJsonAsync<JsonElement>(assignableReviewersHref, cancellationToken);
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

    private static string RequireHref(TagCatalogItem tag, string rel) =>
        tag.Href(rel)
        ?? throw new InvalidOperationException($"The tag '{tag.Name}' advertised no '{rel}' rel (ADR 0543/0555).");

    public async Task RevokeAclEntryAsync(AclEntryInfo entry, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(ApiCore.RequireHref(entry, "remove"), cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    public sealed record TagCatalog(IReadOnlyList<TagCatalogItem> Items, bool CanManage);

    public async Task<TagCatalog> GetTagCatalogWithColorsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("tags", cancellationToken), cancellationToken);
        var items = new List<TagCatalogItem>();
        if (json.TryGetProperty("catalog", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                items.Add(new TagCatalogItem(
                    e.GetProperty("id").GetGuid(),
                    e.GetProperty("name").GetString() ?? "",
                    e.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                    ApiCore.ParseLinks(e)));
            }
        }

        return new TagCatalog(items, json.TryGetProperty("canManage", out var cm) && cm.GetBoolean());
    }

    /// <summary>
    /// A folder's contents AND its persisted contents order, from the one listing that already carries both.
    /// Following rels must not turn one screen into N requests, and the order travelling in the children
    /// envelope is precisely so a client does not have to ask for it separately (ADR 0543, issue #416).
    /// </summary>
    public async Task<(List<Node> Children, int SortOrder)> GetFolderContentsAsync(string childrenHref, CancellationToken cancellationToken = default)
    {
        var sortOrder = 1;
        var first = true;
        var children = await _core.LoadPagedAsync(childrenHref, "children", ParseNode, cancellationToken, page =>
        {
            if (first)
            {
                sortOrder = ReadContentsSortOrder(page);
                first = false;
            }
        });

        return (children, sortOrder);
    }

    /// <summary>
    /// Every address a document advertises, from ONE read (ADR 0543/0555). For a caller that holds an id and
    /// needs several of the document's sub-resources at once — opening a folder wants children, references and
    /// the contents order — this is what keeps "follow a rel" from meaning "fetch the document once per rel".
    /// </summary>
    /// <summary>
    /// One rel, resolved by fetching the resource at its ADVERTISED self address and following what it
    /// offers (ADR 0559): for the caller whose row advertises `self` but not the sub-resource it needs.
    /// Throws when the resource does not offer the rel — that absence is the server's answer (ADR 0543).
    /// </summary>
    public async Task<string> RelViaSelfAsync(string documentSelfHref, string rel, CancellationToken cancellationToken = default) =>
        (await GetDocumentLinksAsync(documentSelfHref, cancellationToken)).TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The document advertised no '{rel}' rel (ADR 0543).");

    public async Task<IReadOnlyDictionary<string, string>> GetDocumentLinksAsync(string documentSelfHref, CancellationToken cancellationToken = default) =>
        ApiCore.ParseLinks(await _core.Http.GetFromJsonAsync<JsonElement>(documentSelfHref, cancellationToken))
        ?? throw new InvalidOperationException($"'{documentSelfHref}' advertised no links at all (ADR 0543).");

    public async Task<List<Comment>> GetCommentsAsync(string chatHref, CancellationToken cancellationToken = default) =>
        (await GetChatAsync(chatHref, cancellationToken)).Messages;

    /// <summary>An inline preview of a check-out's WORKING COPY — what you are about to check in.</summary>
    /// <remarks>
    /// Follows the row's own `preview` rel (ADRs 0543/0555). The rel is absent until a working copy has been
    /// saved, and its absence means exactly that — there is nothing to preview — so it is not an error.
    /// </remarks>
    public async Task<byte[]> DownloadCurrentVersionAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var preview = await GetPreviewAsync(versionsHref, cancellationToken);
        if (preview.DownloadUrl is null)
        {
            throw new ApiActionException("This document has no downloadable version.");
        }

        var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(preview.DownloadUrl, cancellationToken);
        return bytes;
    }

    public async Task CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
    {
        var resp = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("tags", cancellationToken), new { name, color }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await SimplArchiveApiClient.ErrorMessageAsync(resp, "Could not add the tag."));
    }
    // Writes the rights at the address the ROW gave us for writing them — `grant` on a principal being added,
    // `edit` on an entry already there. One method, because it is one operation: the two rels differ only in
    // which side of the same address the server chose to advertise (ADR 0555).
    public async Task SetAclEntryAsync(IAdvertisesLinks row, AclRights rights, CancellationToken cancellationToken = default)
    {
        var href = row.Href("grant") ?? row.Href("edit")
            ?? throw new InvalidOperationException($"The row '{row.Name}' advertised neither 'grant' nor 'edit' — you may not change its access (ADR 0543/0555).");
        using var response = await _core.Http.PutAsJsonAsync(href, rights, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException(Strings.Get("MaInsufficientRights"));
        }

        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }
}
