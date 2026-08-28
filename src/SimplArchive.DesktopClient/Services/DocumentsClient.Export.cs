using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// The combined-export follow (#658), in its own partial: DocumentsClient.cs is on the 1000-line debt list
// (#443's finale owns shrinking it), and the ceiling guard rightly refuses quiet additions to it.
public sealed partial class DocumentsClient
{
    /// <summary>
    /// One combined .vcf/.ics from a uniform selection (#658): follows the bulk collection's `export` rel.
    /// Returns the bytes and the server's file name (which carries the authoritative extension).
    /// </summary>
    public async Task<(byte[] Bytes, string FileName)> ExportCombinedAsync(
        IReadOnlyList<Guid> ids, string name, CancellationToken cancellationToken = default)
    {
        var bulk = await _core.Http.GetFromJsonAsync<System.Text.Json.JsonElement>(
            await _core.RootHrefAsync("documentsBulk", cancellationToken), cancellationToken);
        var exportHref = ApiCore.RelHref(bulk, "export")
            ?? throw new InvalidOperationException("The bulk collection advertised no 'export' rel (ADR 0543).");

        using var response = await _core.Http.PostAsJsonAsync(exportHref, new { ids, name }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? name;
        return (await response.Content.ReadAsByteArrayAsync(cancellationToken), fileName);
    }
}
