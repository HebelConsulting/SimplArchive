using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Conversion;

/// <summary>
/// Asks the OCR sidecar which pages of a batch are Patch 3 separator sheets (issue #492) — a typed HttpClient
/// pointed at the same <c>Ocr:Url</c> as the searchable-PDF and thumbnail routes.
/// </summary>
/// <remarks>
/// Best-effort, exactly like its siblings: any failure yields null, and the caller leaves the scan alone. A
/// batch that silently does not get cut is a nuisance; a batch cut on a guess is somebody's document in two
/// pieces.
/// </remarks>
public sealed class SidecarPatchCodeDetector(HttpClient http, ILogger<SidecarPatchCodeDetector> logger)
    : IPatchCodeDetector
{
    public async Task<IReadOnlyList<int>?> DetectSeparatorPagesAsync(
        byte[] bytes,
        SearchablePdfSourceKind kind,
        CancellationToken cancellationToken = default)
    {
        if (http.BaseAddress is null)
        {
            return null; // Ocr:Url not configured — the whole feature is off
        }

        var (contentType, fileName, kindParam) = kind switch
        {
            SearchablePdfSourceKind.Pdf => ("application/pdf", "in.pdf", "pdf"),
            _ => ("image/tiff", "in.tif", "tiff"),
        };

        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(file, "file", fileName);

            using var response = await http.PostAsync($"patch-codes?kind={kindParam}", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OCR sidecar returned {Status} detecting patch codes in a {Kind}.", (int)response.StatusCode, kind);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<Result>(cancellationToken);
            return result?.PatchPages ?? [];
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "OCR sidecar call failed; this scan will not be cut at its separator sheets.");
            return null;
        }
    }

    private sealed record Result(
        [property: JsonPropertyName("pageCount")] int PageCount,
        [property: JsonPropertyName("patchPages")] IReadOnlyList<int> PatchPages);
}

/// <summary>No sidecar, no detection — the patch-code feature is simply off (see the DI registration).</summary>
public sealed class NullPatchCodeDetector : IPatchCodeDetector
{
    public Task<IReadOnlyList<int>?> DetectSeparatorPagesAsync(
        byte[] bytes,
        SearchablePdfSourceKind kind,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<int>?>(null);
}
