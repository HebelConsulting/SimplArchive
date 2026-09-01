using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Repository archive transfer (#518, the per-area client split): exporting a repository or folder subtree to
/// a .zip, and importing one back. Both are tenant-admin-only server-side. Rides the shared authenticated
/// <see cref="ApiCore"/>.
/// </summary>
/// <remarks>
/// Its own area because an archive is its own subject: the export options describe a FILTER over a subtree and
/// the import result describes what a zip turned into, neither of which is a statement about a document.
/// </remarks>
public sealed class RepositoryArchiveClient(ApiCore core)
{
    private readonly ApiCore _core = core;

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
}
