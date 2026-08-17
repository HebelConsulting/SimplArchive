using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The recycle-bin tab's area (#443, ops tranche): the tenant-wide soft-deleted listing and its bulk
/// restore/purge, addressed from the rows and the collection's own links (ADR 0543/0555/0557).
/// Rides the shared authenticated <see cref="ApiCore"/>.
/// </summary>
public sealed class RecycleBinClient(ApiCore core)
{
    private readonly ApiCore _core = core;

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
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("recycleBin", cancellationToken), cancellationToken);
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
                    ApiCore.ParseLinks(item)));
            }
        }

        return new RecycleBinList(items, ApiCore.ParseLinks(response) ?? new Dictionary<string, string>());
    }

    // Empties the whole tenant-wide recycle bin — permanently purges every soft-deleted document (ADR "Recycle
    // bin tab") — tenant-admin only.
    public async Task PurgeRecycleBinAsync(string purgeAllHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(purgeAllHref, null, cancellationToken);
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
        using var response = await _core.Http.PostAsJsonAsync(restoreSelectedHref, new { ids }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (json.GetProperty("restored").GetInt32(), json.GetProperty("skipped").GetInt32());
    }

    // Bulk purge of selected items (ADR "Bulk purge of selected recycle-bin items") — tenant-admin; permanently
    // removes each requested recycle-bin root + subtree; returns purged vs skipped (gone / active / held / WORM).
    public async Task<(int Purged, int Skipped)> PurgeManyAsync(string purgeSelectedHref, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(purgeSelectedHref, new { ids }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("Only a tenant administrator can purge items.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (json.GetProperty("purged").GetInt32(), json.GetProperty("skipped").GetInt32());
    }
}
