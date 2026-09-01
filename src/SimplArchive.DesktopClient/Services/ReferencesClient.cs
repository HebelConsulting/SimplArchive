using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// References — the "shortcut" area (#518, the per-area client split): the references filed in a folder, the
/// folders that reference a given item, and creating or removing one. Rides the shared authenticated
/// <see cref="ApiCore"/> like every sibling client.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="DocumentsClient"/>, which was on the 1000-line debt list. References are their own
/// subject: a shortcut row stands for a real document elsewhere, which is why <c>Reference</c> carries the
/// TARGET's list-row columns (#768) rather than a stub's.
/// </para>
/// <para>
/// <c>SetPrimaryLocationAsync</c> deliberately did NOT come with them. Promoting a referencing folder to be the
/// primary location is a REPARENT — it shares MoveAsync's If-Match contract, and it is one of the several paths
/// that change a document's parent. Those belong together in DocumentsClient: a rule about reparenting that
/// lives in one of two files is a rule that gets applied in one of two places.
/// </para>
/// </remarks>
public sealed class ReferencesClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    // A folder that references a given item, with its full display path — see ADR "References-of-an-item list".
    // OpenHref is the row's own `open` address (ADR 0555) — null where the server withheld it.
    public sealed record ReferencingFolder(Guid Id, string Name, string Path, string? OpenHref = null);

    // The references-of-an-item view: the document's real primary location (null when it's a repository root or
    // the caller can't see the parent) plus the folders that reference it (ADR 0506).
    public sealed record ReferencesView(ReferencingFolder? Primary, IReadOnlyList<ReferencingFolder> Folders);

    // TargetId/Name/HasVersions/HasSubfolders describe the referenced item; ReferenceId identifies the
    // shortcut row (for delete); RealParentId is the target's real home folder (for "Go to …").
    // DeleteHref is the shortcut row's own `delete` address (ADR 0543) — the pair of ids that used to rebuild
    // it are still here because the tree needs them, but nothing composes a URL out of them any more.
    public sealed record Reference(
        Guid ReferenceId, Guid TargetId, string Name, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, Guid? RealParentId,
        string? DeleteHref = null, IReadOnlyDictionary<string, string>? Links = null,
        // The TARGET's list-row columns, exactly as a children row carries them (#768). Without these a
        // shortcut row drew blank Type / Doc date / Size / Tags / Owner cells beside a real row that filled
        // them — the same defect on both clients, from the same missing projection.
        string DocumentType = "", DateOnly? DocumentDate = null, long? SizeBytes = null,
        IReadOnlyList<string>? Tags = null, string CreatedBy = "", string SensitivityLabelName = "",
        string? SensitivityLabelColor = null, int VersionCount = 0, DateTimeOffset? VersionCreatedAt = null,
        string? Icon = null);

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
        ApiCore.ParseLinks(item),
        item.TryGetProperty("documentType", out var dt) ? dt.GetString() ?? "" : "",
        item.TryGetProperty("documentDate", out var dd) && dd.ValueKind == JsonValueKind.String && DateOnly.TryParse(dd.GetString(), out var date) ? date : null,
        item.TryGetProperty("sizeBytes", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : null,
        item.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array ? tg.EnumerateArray().Select(x => x.GetString() ?? "").Where(v => v.Length > 0).ToList() : [],
        item.TryGetProperty("createdBy", out var cb) ? cb.GetString() ?? "" : "",
        item.TryGetProperty("sensitivityLabelName", out var sln) ? sln.GetString() ?? "" : "",
        item.TryGetProperty("sensitivityLabelColor", out var slc) && slc.ValueKind == JsonValueKind.String ? slc.GetString() : null,
        item.TryGetProperty("versionCount", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt32() : 0,
        item.TryGetProperty("versionCreatedAt", out var vca) && vca.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(vca.GetString(), out var vcaDt) ? vcaDt : null,
        item.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null);
}
