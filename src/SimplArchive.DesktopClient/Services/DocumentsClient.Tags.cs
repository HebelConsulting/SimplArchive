using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// The tag CATALOG admin surface (ADR "Tag controlled vocabulary", #416), in its own partial: create, rename,
// recolour, retire and merge a tenant's tags, plus the two records the catalog is read into.
//
// Split out of DocumentsClient.cs because that file is on the 1000-line debt list and the ceiling guard rightly
// refuses quiet additions to it — #858 needed ten lines there for the conditional `move` rel, and CLAUDE.md's
// rule is that an over-limit class is a standing debt, not a licence. Following the DocumentsClient.Export.cs
// precedent, the answer is to take something OUT rather than to raise the ceiling.
//
// These members were INTERLEAVED with unrelated ones rather than sitting in a block, which is itself the
// argument for the move: a section header three-quarters of the way down a 1,466-line file had stopped
// describing where its members actually were.
public sealed partial class DocumentsClient
{
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

    private static string RequireHref(TagCatalogItem tag, string rel) =>
        tag.Href(rel)
        ?? throw new InvalidOperationException($"The tag '{tag.Name}' advertised no '{rel}' rel (ADR 0543/0555).");

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

    public async Task CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
    {
        var resp = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("tags", cancellationToken), new { name, color }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await SimplArchiveApiClient.ErrorMessageAsync(resp, "Could not add the tag."));
    }
}
