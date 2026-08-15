using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimplArchive.Localization;

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
public sealed class InboxApi(HttpClient http, SimplArchiveApiClient client)
{
    // The inbox listing: its items AND the collection's own actions (ADR 0557) — `join` lives here because it
    // acts on a SELECTION, so it belongs to the collection rather than to any one row. Read once, followed many
    // times; re-fetching the inbox to learn an address it already handed over is a round trip spent re-learning
    // something in hand.
    public sealed record InboxListing(IReadOnlyList<SimplArchiveApiClient.InboxItemInfo> Items, IReadOnlyDictionary<string, string> Links)
    {
        public string? Href(string rel) => Links.TryGetValue(rel, out var href) ? href : null;
    }

    // Lists inbox items (ADR "S3-backed inbox"). Own-items-only by default; includeGroups also aggregates the
    // caller's group inboxes, and user opens a specific user's inbox for a CanManageInboxes holder (ADR 0532).
    public async Task<InboxListing> ListAsync(bool includeGroups = false, Guid? user = null, CancellationToken cancellationToken = default)
    {
        // One advertised address, three views of it — a filter is a query parameter, not a different route.
        var inbox = await client.RootHrefAsync("inbox", cancellationToken);
        var url = user is { } viewUser ? $"{inbox}?user={viewUser}" : includeGroups ? $"{inbox}?includeGroups=true" : inbox;
        var json = await http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
        var items = new List<SimplArchiveApiClient.InboxItemInfo>();
        if (json.TryGetProperty("items", out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                string Link(string rel) => item.TryGetProperty("links", out var links)
                    ? links.EnumerateArray().FirstOrDefault(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString() ?? ""
                    : "";
                Guid? Id(string prop) => item.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null;
                string? Str(string prop) => item.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                items.Add(new SimplArchiveApiClient.InboxItemInfo(
                    item.GetProperty("name").GetString() ?? "",
                    item.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    Link("download"),
                    item.TryGetProperty("hasMask", out var hm) && hm.GetBoolean(),
                    Id("groupId"), Str("groupName"), Id("userId"), Str("userName"), Link("move"), SimplArchiveApiClient.ParseLinks(item),
                    // Answered by the listing from a sidecar's existence, so it costs no extra request (#491).
                    item.TryGetProperty("signed", out var sg) && sg.ValueKind == JsonValueKind.True));
            }
        }

        return new InboxListing(items, SimplArchiveApiClient.ParseLinks(json) ?? new Dictionary<string, string>());
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
    public async Task<PagesInfo?> GetAsync(
        SimplArchiveApiClient.InboxItemInfo item,
        CancellationToken cancellationToken = default)
    {
        if (item.Href("pages") is not { } pagesHref)
        {
            return null;
        }

        using var response = await http.GetAsync(pagesHref.TrimStart('/'), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var links = SimplArchiveApiClient.ParseLinks(json) ?? new Dictionary<string, string>();
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
            var me = await http.GetFromJsonAsync<JsonElement>(await client.RootHrefAsync("me", cancellationToken), cancellationToken);
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
        var response = await (await http.PostAsJsonAsync(await client.RootHrefAsync("inbox", cancellationToken), new { fileName }, cancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var uploadUrl = response.GetProperty("uploadUrl").GetString()!;
        using var content = new ByteArrayContent(bytes);
        (await SimplArchiveApiClient.Anonymous.PutAsync(uploadUrl, content, cancellationToken)).EnsureSuccessStatusCode();

        // A server that does not advertise it gets no signal, and the sweep still catches the file — a missing
        // rel means "not available here", never a composed URL.
        if (SimplArchiveApiClient.RelHref(response, "processed") is { } processed)
        {
            (await http.PostAsJsonAsync(processed, new { }, cancellationToken)).EnsureSuccessStatusCode();
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
        var me = await http.GetFromJsonAsync<JsonElement>(await client.RootHrefAsync("me", cancellationToken), cancellationToken);
        var href = SimplArchiveApiClient.ParseLinks(me)?.GetValueOrDefault(rel)
            ?? throw new ApiActionException(Strings.Get("ApiErrGeneric"));

        using var response = await http.PutAsJsonAsync(href.TrimStart('/'), new { enabled }, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
    }

    /// <summary>
    /// Straightens the item on demand, returning its new name — a TIFF comes back a PDF, because straightening
    /// re-renders the pages and the converter only emits PDF.
    /// </summary>
    public async Task<string> DeskewAsync(string deskewHref, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(deskewHref.TrimStart('/'), new { }, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
        return (await NamesAsync(response, cancellationToken)).FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Fetches an advertised href's bytes — the printable separator sheet and its sample batch.</summary>
    public Task<byte[]> GetBytesAsync(string href, CancellationToken cancellationToken = default) =>
        http.GetByteArrayAsync(href.TrimStart('/'), cancellationToken);

    /// <summary>
    /// Cuts a batch scan at its separator sheets and returns the resulting items' names (#492). The batch
    /// itself is kept, renamed with a "_to_be_deleted" suffix.
    /// </summary>
    public async Task<IReadOnlyList<string>> CutAtPatchCodesAsync(string patchCodesHref, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(patchCodesHref.TrimStart('/'), new { }, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
        return await NamesAsync(response, cancellationToken);
    }

    /// <summary>Splits the item into one inbox item per page and returns their names. The source is kept.</summary>
    public async Task<IReadOnlyList<string>> SplitAsync(string splitHref, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(splitHref.TrimStart('/'), new { }, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
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
        using var response = await http.PostAsJsonAsync(sortHref.TrimStart('/'), body, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
    }

    /// <summary>Joins several items into one, in the order given, and returns its name. The sources are kept.</summary>
    public async Task<string> JoinAsync(
        string joinHref,
        IReadOnlyList<string> names,
        string? name,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(joinHref.TrimStart('/'), new { names, name }, cancellationToken);
        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("ApiErrGeneric"), cancellationToken);
        return (await NamesAsync(response, cancellationToken)).FirstOrDefault() ?? string.Empty;
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(n => n.GetString() ?? string.Empty).ToList()
            : [];
    }
}
