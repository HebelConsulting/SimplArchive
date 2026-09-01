using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Free-form document tags (#518, the per-area client split): a document's tags, the tenant tag catalog that
/// backs the add-dialog's suggestions, and replacing the set. Rides the shared authenticated
/// <see cref="ApiCore"/>.
/// </summary>
/// <remarks>
/// The tags resource is ONE address, read or replaced — the GET and the PUT take the same advertised href
/// (ADR 0543, 0719's "one rel per resource, the method says which action"), which is why they belong in one
/// place rather than beside whatever happened to need them.
/// </remarks>
public sealed class TagsClient(ApiCore core)
{
    private readonly ApiCore _core = core;

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

    private static IReadOnlyList<string> ReadTags(JsonElement json) =>
        json.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];

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
}
