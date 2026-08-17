using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimplArchive.Localization;
using System.Net;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The inbox's api surface (issue #487, ADR 0575): listing what is staged, and the page operations — what an
/// item's pages are, and splitting, sorting or joining them.
/// </summary>
/// <remarks>
/// <para>
/// Its own class rather than more methods on <see cref="SimplArchiveApiClient"/>, which is on the 1000-line
/// standing-debt list and may only get smaller (issue #466). The listing moved here with the page operations
/// rather than staying behind: they are one subject, and splitting a subject across two files to satisfy a
/// line count is how a class ends up with no describable responsibility at all. Reached as
/// <c>api.Inbox.…</c>, sharing the same authenticated <see cref="HttpClient"/>.
/// </para>
/// <para>
/// Every address here was ADVERTISED — the row's <c>pages</c> rel, and the <c>split</c>/<c>sort</c> rels that
/// resource returns — so nothing composes a URL (ADR 0543), and no id is turned back into a resource to find
/// one (ADR 0555/0557).
/// </para>
/// </remarks>
public sealed class InboxApi(ApiCore core)
{
    // The inbox listing: its items AND the collection's own actions (ADR 0557) — `join` lives here because it
    // acts on a SELECTION, so it belongs to the collection rather than to any one row. Read once, followed many
    // times; re-fetching the inbox to learn an address it already handed over is a round trip spent re-learning
    // something in hand.
    public sealed record InboxListing(IReadOnlyList<InboxItemInfo> Items, IReadOnlyDictionary<string, string> Links)
    {
        public string? Href(string rel) => Links.TryGetValue(rel, out var href) ? href : null;
    }

    // Lists inbox items (ADR "S3-backed inbox"). Own-items-only by default; includeGroups also aggregates the
    // caller's group inboxes, and user opens a specific user's inbox for a CanManageInboxes holder (ADR 0532).
    public async Task<InboxListing> ListAsync(bool includeGroups = false, Guid? user = null, CancellationToken cancellationToken = default)
    {
        // One advertised address, three views of it — a filter is a query parameter, not a different route.
        var inbox = await core.RootHrefAsync("inbox", cancellationToken);
        var url = user is { } viewUser ? $"{inbox}?user={viewUser}" : includeGroups ? $"{inbox}?includeGroups=true" : inbox;
        var json = await core.Http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
        var items = new List<InboxItemInfo>();
        if (json.TryGetProperty("items", out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                string Link(string rel) => item.TryGetProperty("links", out var links)
                    ? links.EnumerateArray().FirstOrDefault(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString() ?? ""
                    : "";
                Guid? Id(string prop) => item.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null;
                string? Str(string prop) => item.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                items.Add(new InboxItemInfo(
                    item.GetProperty("name").GetString() ?? "",
                    item.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    Link("download"),
                    item.TryGetProperty("hasMask", out var hm) && hm.GetBoolean(),
                    Id("groupId"), Str("groupName"), Id("userId"), Str("userName"), Link("move"), ApiCore.ParseLinks(item),
                    // Answered by the listing from a sidecar's existence, so it costs no extra request (#491).
                    item.TryGetProperty("signed", out var sg) && sg.ValueKind == JsonValueKind.True));
            }
        }

        return new InboxListing(items, ApiCore.ParseLinks(json) ?? new Dictionary<string, string>());
    }

    /// <summary>
    /// What one staged item's pages look like, plus what can be done with them. A missing href means the server
    /// did not offer it — a one-page file has no split and no sort — so the client disables the affordance
    /// rather than offering a button that fails on click (ADR 0554).
    /// </summary>
    public sealed record PagesInfo(
        string Format,
        int PageCount,
        string? SplitHref,
        string? SortHref,
        string? DeskewHref = null,
        bool Signed = false,
        string? PatchCodesHref = null)
    {
        public bool CanSplit => SplitHref is not null;

        public bool CanSort => SortHref is not null;

        public bool CanDeskew => DeskewHref is not null;

        public bool CanCutAtPatchCodes => PatchCodesHref is not null;
    }

    /// <summary>Null when the row advertised no <c>pages</c> rel at all — no request is made in that case.</summary>
    public Task<PagesInfo?> GetAsync(
        InboxItemInfo item,
        CancellationToken cancellationToken = default) =>
        item.Href("pages") is { } pagesHref ? GetAsync(pagesHref, cancellationToken) : Task.FromResult<PagesInfo?>(null);

    /// <summary>
    /// The pages resource at an advertised href. Overload shared with the Check-out tab (ADR 0593): a check-out
    /// row's `pages` rel answers the same protocol for the WORKING COPY, so the parse lives once here rather
    /// than as a second copy that can drift.
    /// </summary>
    public async Task<PagesInfo?> GetAsync(string pagesHref, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.GetAsync(pagesHref.TrimStart('/'), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var links = ApiCore.ParseLinks(json) ?? new Dictionary<string, string>();
        return new PagesInfo(
            json.TryGetProperty("format", out var f) ? f.GetString() ?? string.Empty : string.Empty,
            json.TryGetProperty("pageCount", out var c) ? c.GetInt32() : 0,
            links.TryGetValue("split", out var split) ? split : null,
            links.TryGetValue("sort", out var sort) ? sort : null,
            links.TryGetValue("deskew", out var deskew) ? deskew : null,
            json.TryGetProperty("signed", out var signed) && signed.ValueKind == JsonValueKind.True,
            links.TryGetValue("patchCodes", out var patch) ? patch : null);
    }

    /// <summary>The inbox ribbon's two standing preferences, as the "me" resource reports them.</summary>
    public sealed record InboxPreferences(bool Deskew, bool CutAtPatchCodes, bool Rotate);

    /// <summary>
    /// Both preferences from <b>one</b> read of "me" (ADR 0557). Defaults to on when the resource cannot be
    /// read, matching the server's own defaults — a ribbon that comes up showing "off" would be a claim the
    /// server never made, and the user would turn back on something that was never off.
    /// </summary>
    public async Task<InboxPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var me = await core.Http.GetFromJsonAsync<JsonElement>(await core.RootHrefAsync("me", cancellationToken), cancellationToken);
            return new InboxPreferences(IsOn(me, "deskewInboxUploads"), IsOn(me, "cutInboxUploadsAtPatchCodes"), IsOn(me, "rotateInboxUploads"));
        }
        catch (Exception)
        {
            return new InboxPreferences(true, true, true);
        }

        static bool IsOn(JsonElement me, string property) =>
            !me.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.False;
    }

    /// <summary>Turns automatic straightening on or off (#491).</summary>
    public Task SetDeskewPreferenceAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetPreferenceAsync("deskewPreference", enabled, cancellationToken);

    // Uploads a local file into the server inbox: POST for a presigned URL, PUT the bytes to it, then FOLLOW
    // the response's `processed` rel so the ingest pipeline — deskew, patch-code cutting — runs now.
    //
    // That last step was missing entirely, in both clients: the endpoint existed and worked, but no resource
    // advertised the rel that reaches it (ADR 0543), so every upload waited up to InboxIngestSweepWorker's
    // five-minute poll. The visible symptoms were "the split documents do not show up" and "the crooked page
    // was not straightened" — one missing link, two bug reports.
    public async Task UploadAsync(string fileName, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var response = await (await core.Http.PostAsJsonAsync(await core.RootHrefAsync("inbox", cancellationToken), new { fileName }, cancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var uploadUrl = response.GetProperty("uploadUrl").GetString()!;
        using var content = new ByteArrayContent(bytes);
        (await ApiCore.Anonymous.PutAsync(uploadUrl, content, cancellationToken)).EnsureSuccessStatusCode();

        // A server that does not advertise it gets no signal, and the sweep still catches the file — a missing
        // rel means "not available here", never a composed URL.
        if (ApiCore.RelHref(response, "processed") is { } processed)
        {
            (await core.Http.PostAsJsonAsync(processed, new { }, cancellationToken)).EnsureSuccessStatusCode();
        }
    }

    /// <summary>Turns automatic rotation of upside-down pages on or off (#492).</summary>
    public Task SetRotatePreferenceAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetPreferenceAsync("rotatePreference", enabled, cancellationToken);

    /// <summary>Turns automatic cutting at separator sheets on or off (#492).</summary>
    public Task SetPatchCodePreferenceAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetPreferenceAsync("patchCodePreference", enabled, cancellationToken);

    // Which rel differs; nothing else does. Following the rel the "me" resource advertises rather than composing
    // its URL is ADR 0543 — and it is why adding the second preference cost one line rather than a second copy.
    private async Task SetPreferenceAsync(string rel, bool enabled, CancellationToken cancellationToken)
    {
        var me = await core.Http.GetFromJsonAsync<JsonElement>(await core.RootHrefAsync("me", cancellationToken), cancellationToken);
        var href = ApiCore.ParseLinks(me)?.GetValueOrDefault(rel)
            ?? throw new ApiActionException(Strings.Get("ApiErrGeneric"));

        using var response = await core.Http.PutAsJsonAsync(href.TrimStart('/'), new { enabled }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
    }

    /// <summary>
    /// Straightens the item on demand, returning its new name — a TIFF comes back a PDF, because straightening
    /// re-renders the pages and the converter only emits PDF.
    /// </summary>
    public async Task<string> DeskewAsync(string deskewHref, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(deskewHref.TrimStart('/'), new { }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
        return (await NamesAsync(response, cancellationToken)).FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Fetches an advertised href's bytes — the printable separator sheet and its sample batch.</summary>
    public Task<byte[]> GetBytesAsync(string href, CancellationToken cancellationToken = default) =>
        core.Http.GetByteArrayAsync(href.TrimStart('/'), cancellationToken);

    /// <summary>
    /// Cuts a batch scan at its separator sheets and returns the resulting items' names (#492). The batch
    /// itself is kept, renamed with a "_to_be_deleted" suffix.
    /// </summary>
    public async Task<IReadOnlyList<string>> CutAtPatchCodesAsync(string patchCodesHref, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(patchCodesHref.TrimStart('/'), new { }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
        return await NamesAsync(response, cancellationToken);
    }

    /// <summary>Splits the item into one inbox item per page and returns their names. The source is kept.</summary>
    public async Task<IReadOnlyList<string>> SplitAsync(string splitHref, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(splitHref.TrimStart('/'), new { }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
        return await NamesAsync(response, cancellationToken);
    }

    /// <summary>
    /// Rewrites the item's pages in the given order (1-based, each page exactly once), optionally turning
    /// individual pages by quarter turns (#522) — one request writes the whole arrangement.
    /// </summary>
    public async Task SortAsync(string sortHref, IReadOnlyList<int> pageOrder, IReadOnlyDictionary<int, int>? rotations = null, CancellationToken cancellationToken = default)
    {
        object body = rotations is { Count: > 0 }
            ? new { pageOrder, rotations = rotations.Select(r => new { page = r.Key, degrees = r.Value }).ToList() }
            : new { pageOrder };
        using var response = await core.Http.PostAsJsonAsync(sortHref.TrimStart('/'), body, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
    }

    /// <summary>Joins several items into one, in the order given, and returns its name. The sources are kept.</summary>
    public async Task<string> JoinAsync(
        string joinHref,
        IReadOnlyList<string> names,
        string? name,
        CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(joinHref.TrimStart('/'), new { names, name }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
        return (await NamesAsync(response, cancellationToken)).FirstOrDefault() ?? string.Empty;
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(n => n.GetString() ?? string.Empty).ToList()
            : [];
    }

    // ---- Moved from the god client (#443, tranche 2) ------------------------------------------------

    public async Task<IReadOnlyList<InboxTargetInfo>> GetInboxGroupsAsync(CancellationToken cancellationToken = default) =>
        await GetInboxTargetsAsync(await core.RootHrefAsync("inboxGroups", cancellationToken), "groups", isGroup: true, cancellationToken);

    // The other active tenant users (ADR 0532) — the "Send to a user" choices, and the admin user-picker list.
    public async Task<IReadOnlyList<InboxTargetInfo>> GetInboxUsersAsync(CancellationToken cancellationToken = default) =>
        await GetInboxTargetsAsync(await core.RootHrefAsync("inboxUsers", cancellationToken), "users", isGroup: false, cancellationToken);

    private async Task<IReadOnlyList<InboxTargetInfo>> GetInboxTargetsAsync(string url, string arrayProp, bool isGroup, CancellationToken cancellationToken)
    {
        var json = await core.Http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
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
        using var response = await core.Http.PostAsJsonAsync(moveUrl, new { targetGroupId, targetUserId }, cancellationToken);
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
        using var response = await core.Http.GetAsync(RequireHref(item, "preview"), cancellationToken);
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
        var json = await core.Http.GetFromJsonAsync<JsonElement>(RequireHref(item, "mask"), cancellationToken);
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
        (await core.Http.PutAsJsonAsync(RequireHref(item, "mask"), body, cancellationToken)).EnsureSuccessStatusCode();
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
        var inbox = await core.Http.GetFromJsonAsync<JsonElement>(await core.RootHrefAsync("inbox", cancellationToken), cancellationToken);
        var href = inbox.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "from-document")
            .GetProperty("href").GetString()
            ?? throw new ApiActionException("The inbox did not offer a template copy here.");

        using var response = await core.Http.PostAsJsonAsync(href, new { documentId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("Your inbox already holds an item with that name, or the document has no version to copy.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task FileInboxItemAsync(InboxItemInfo item, Guid folderId, string? comment = null, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(RequireHref(item, "file"), new { folderId, comment }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to file into that folder.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Files the inbox item as a new version of an existing document (ADR "Context-aware inbox filing dialog").
    public async Task FileInboxItemAsVersionAsync(InboxItemInfo item, Guid documentId, string? comment = null, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(RequireHref(item, "file"), new { documentId, comment }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to add a version to that document.");
        }

        response.EnsureSuccessStatusCode();
    }

    // The item's OWN address, which the listing advertises as `self` with DELETE as its method.
    public Task DeleteInboxItemAsync(InboxItemInfo item, CancellationToken cancellationToken = default) =>
        core.Http.DeleteAsync(RequireHref(item, "self"), cancellationToken);


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
    // Addressed from the row the LISTING advertised, never composed (ADR 0543/0555).
    private static string RequireHref(InboxItemInfo item, string rel) =>
        item.Href(rel)
        ?? throw new InvalidOperationException($"The inbox item '{item.Name}' advertised no '{rel}' rel (ADR 0543).");
}
