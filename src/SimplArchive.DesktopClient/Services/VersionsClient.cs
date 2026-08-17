using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The versions area (#443, ops tranche): a document's version rows and what they advertise — restore,
/// document date, comparison, downloads — plus the preview/text-layout reads that resolve through the
/// versions collection. Rides the shared authenticated <see cref="ApiCore"/>.
/// </summary>
public sealed class VersionsClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"). Coordinates are normalized 0..1
    // within each page (top-left origin); the client scales them to the rendered page size.
    public sealed record TextLayoutBox(string Text, double X, double Y, double Width, double Height);

    public sealed record TextLayoutPageInfo(IReadOnlyList<TextLayoutBox> Words);

    public sealed record TextLayoutInfo(IReadOnlyList<TextLayoutPageInfo> Pages);
    // The document's current version JsonElement honoring the server's currentVersionId pointer (ADR
    // "Version-restore via a current-version pointer", issue #265), else the latest confirmed. Returns the
    // element + its version number, or null when there's no confirmed version.
    internal static (JsonElement Version, int Number)? PickCurrentVersionElement(JsonElement response)
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
    // Sets a version's document (issuing) date ("yyyy-MM-dd") at the address the version row advertised.
    public async Task SetDocumentDateAsync(string documentDateHref, string documentDate, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(documentDateHref, new { documentDate }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the document date ({(int)response.StatusCode}).");
        }
    }
    // A preview from an ALREADY-ADVERTISED versions address, for a caller holding a row rather than an id
    // (#462). Same body as GetPreviewAsync below the first line; that one has to turn an id back into the
    // address first, which is the round trip a row-holder should not pay (ADR 0557).
    public async Task<Preview> GetPreviewFromVersionsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(versionsHref.TrimStart('/'), cancellationToken);
        if (PickCurrentVersionElement(response) is not { } picked)
        {
            return new Preview(null, false, null, null, null, "");
        }

        var confirmed = picked.Version;
        var converted = confirmed.TryGetProperty("previewConverted", out var pc) && pc.GetBoolean();
        var extension = confirmed.TryGetProperty("fileExtension", out var fe) ? fe.GetString() ?? "" : "";
        return new Preview(ApiCore.FindLink(confirmed, "preview"), converted, ApiCore.FindLink(confirmed, "download"), ApiCore.FindLink(confirmed, "text-layout"), ApiCore.FindLink(confirmed, "preview-pages"), extension, ApiCore.FindLink(confirmed, "annotations"));
    }

    // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages"); null (204) for
    // every other format, where the caller uses the single preview URL.
    public async Task<IReadOnlyList<string>?> GetPreviewPagesAsync(string previewPagesUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.GetAsync(previewPagesUrl.TrimStart('/'), cancellationToken);
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
        using var response = await _core.Http.GetAsync(textLayoutUrl.TrimStart('/'), cancellationToken);
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
    // ---- Version comparison (ADR "Document version comparison") ----
    // A version row, carrying the links its own row advertised — `restore` and `document-date` are followed
    // from here rather than rebuilt from a document id and a version id (ADR 0543/0555).

    public sealed record VersionInfo(Guid Id, int? VersionNumber, string Status, string FileExtension, string? DownloadUrl,
        string DocumentDate = "", DateTimeOffset CreatedAt = default, string CreatedByName = "", bool IsCurrent = false,
        string? Comment = null, IReadOnlyDictionary<string, string>? Links = null, string? WorkflowStatus = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }
    // Restores (rolls back to) an earlier version (ADR "Version restore") — creates a new current version from
    // its content. Throws on a rejected request (403 no edit rights, 409 workflow/hold/checkout).
    public async Task RestoreVersionAsync(VersionInfo version, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(version, "restore"), null, cancellationToken);
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
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        var compareHref = ApiCore.ParseLinks(json) is { } links && links.TryGetValue("compare", out var href) ? href : null;
        return (await GetVersionsAsync(versionsHref, cancellationToken), compareHref);
    }

    public async Task<List<VersionInfo>> GetVersionsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
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
                    Links: ApiCore.ParseLinks(v), WorkflowStatus: SimplArchiveApiClient.StrOrNull(v, "workflowStatus")));
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
        var json = await _core.Http.GetFromJsonAsync<JsonElement>($"{compareHref}?from={fromVersionId}&to={toVersionId}", cancellationToken);
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
        var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(downloadUrl, cancellationToken);
        return bytes;
    }
    private static string RequireHref(VersionInfo version, string rel) =>
        version.Href(rel)
        ?? throw new InvalidOperationException($"Version {version.VersionNumber} advertised no '{rel}' rel — only a confirmed version offers one (ADR 0543/0555).");
}
