using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The search area's client (#443, tranche 3): free-text/filtered/faceted search, the field catalog, and the
/// saved searches with their sharing, over the shared authenticated <see cref="ApiCore"/>. Reached as
/// <c>api.Search</c>. Hit rows carry their own advertised addresses (ADR 0555), so nothing here composes.
/// </summary>
public sealed class SearchClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    // Free-text metadata search across the tenant (names + index-field values) — see ADR "Metadata search
    // (first slice)". Follows the next links to load all pages.
    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        await _core.LoadPagedAsync($"{await _core.RootHrefAsync("search", cancellationToken)}?q={Uri.EscapeDataString(query)}", "results", ParseSearchResult, cancellationToken);

    // Runs a search from a pre-assembled query string (q + repositoryId + system[..]/fields[..] filters) —
    // see ADR "Search-refinement UI".
    public async Task<List<SearchResult>> SearchWithFiltersAsync(string queryString, CancellationToken cancellationToken = default) =>
        await _core.LoadPagedAsync($"{await _core.RootHrefAsync("search", cancellationToken)}?{queryString}", "results", ParseSearchResult, cancellationToken);

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
        string? next = $"{await _core.RootHrefAsync("search", cancellationToken)}?{queryString}";
        var first = true;
        while (next is not null)
        {
            var page = await _core.Http.GetFromJsonAsync<JsonElement>(next, cancellationToken);
            if (page.TryGetProperty("results", out var array))
            {
                results.AddRange(array.EnumerateArray().Select(ParseSearchResult));
            }

            if (first)
            {
                facets = ParseFacets(page);
                first = false;
            }

            next = ApiCore.FindLink(page, "next");
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
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("savedSearches", cancellationToken), cancellationToken);
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
                    ApiCore.ParseLinks(s)));
            }
        }

        return list;
    }

    // The picker options (active users + groups) for the share dialog.
    public async Task<List<ShareTargetInfo>> GetShareTargetsAsync(CancellationToken cancellationToken = default)
    {
        // `share-targets` is advertised by the saved-searches collection — the dialog that needs it opens from
        // that list, so the read is one the screen has effectively already paid for.
        var collection = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("savedSearches", cancellationToken), cancellationToken);
        var targetsHref = ApiCore.ParseLinks(collection) is { } collectionLinks && collectionLinks.TryGetValue("share-targets", out var t)
            ? t
            : throw new InvalidOperationException("Saved searches advertised no 'share-targets' rel (ADR 0543).");

        var json = await _core.Http.GetFromJsonAsync<JsonElement>(targetsHref, cancellationToken);
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
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(RequireHref(search, "shares"), cancellationToken);
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
        using var response = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("savedSearches", cancellationToken), new { name, queryString }, cancellationToken);
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
        using var response = await _core.Http.PutAsJsonAsync(
            RequireHref(search, "self"),
            new { name = search.Name, queryString = search.QueryString, shareScope, shares = shares.Select(s => new { type = s.Type, id = s.Id }) },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteSavedSearchAsync(SavedSearchInfo search, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(RequireHref(search, "delete"), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // The tenant's distinct index-field names + types, for the refinement UI's field picker.
    public sealed record SearchField(string Name, int DataType);

    public async Task<IReadOnlyList<SearchField>> GetSearchFieldsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("searchFields", cancellationToken), cancellationToken);
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

    // A metadata-search hit — see ADR "Metadata search (first slice)". ParentId is the item's home folder
    // (null = a repository root), for navigating to it.
    // VersionsHref is the address the HIT advertised (#462) — the row carries its own addresses, so previewing a
    // result follows what the listing handed over instead of resolving the document again (ADR 0555/0557). Null
    // for a folder, which advertises no `versions` because it has nothing to preview.
    public sealed record SearchResult(Guid Id, string Name, bool IsFolder, Guid? ParentId, string Path, string Highlight, string? VersionsHref = null, IReadOnlyDictionary<string, string>? Links = null);


    private static SearchResult ParseSearchResult(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("isFolder", out var f) && f.GetBoolean(),
        item.TryGetProperty("parentId", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetGuid() : null,
        item.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
        item.TryGetProperty("highlight", out var hl) ? hl.GetString() ?? "" : "",
        ApiCore.FindLink(item, "versions"),
        ApiCore.ParseLinks(item));

    private static string RequireHref(SavedSearchInfo search, string rel) =>
        search.Href(rel)
        ?? throw new InvalidOperationException($"The saved search advertised no '{rel}' rel — it is not yours to change (ADR 0543/0555).");
}
