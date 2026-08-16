using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The annotations area (#443, tranche 5): notes and shapes on a version's pages, always addressed by the advertised annotations url. Rides the shared authenticated <see cref="ApiCore"/> (#443).
/// </summary>
public sealed class AnnotationsClient(ApiCore core)
{
    private readonly ApiCore _core = core;


    // A sticky note / positional annotation (ADR "Document annotations"). Etag is the optimistic-concurrency
    // token to send back as If-Match on edit/delete; CanEdit/CanDelete are the server's per-caller hints.
    // Points is the normalized "x,y x,y …" (each 0..1) poly-line for a Freehand (kind 7), null otherwise (ADR 0525).
    public sealed record AnnotationInfo(Guid Id, int PageIndex, int Kind, double PositionX, double PositionY, double? Width, double? Height, string Text, string Color, string AuthorName, string Etag, bool CanEdit, bool CanDelete, string? Points = null);

    // --- Sticky notes / annotations (ADR "Document annotations") ----------------------------------------

    // The annotation list + whether the caller may create a note here (CanAnnotate, ADR "CanAnnotate right").
    public sealed record AnnotationList(IReadOnlyList<AnnotationInfo> Items, bool CanCreate);

    public async Task<AnnotationList> GetAnnotationsAsync(string annotationsUrl, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(annotationsUrl.TrimStart('/'), cancellationToken);
        var result = new List<AnnotationInfo>();
        if (json.TryGetProperty("annotations", out var arr))
        {
            foreach (var a in arr.EnumerateArray())
            {
                result.Add(new AnnotationInfo(
                    a.GetProperty("id").GetGuid(),
                    a.GetProperty("pageIndex").GetInt32(),
                    a.TryGetProperty("kind", out var k) ? k.GetInt32() : 0,
                    a.GetProperty("positionX").GetDouble(),
                    a.GetProperty("positionY").GetDouble(),
                    a.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : null,
                    a.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : null,
                    a.GetProperty("text").GetString() ?? "",
                    a.GetProperty("color").GetString() ?? "#FFEB3B",
                    a.TryGetProperty("authorName", out var an) ? an.GetString() ?? "" : "",
                    a.TryGetProperty("etag", out var et) ? et.GetString() ?? "" : "",
                    a.TryGetProperty("canEdit", out var ce) && ce.GetBoolean(),
                    a.TryGetProperty("canDelete", out var cd) && cd.GetBoolean(),
                    a.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.String ? pts.GetString() : null));
            }
        }

        return new AnnotationList(result, json.TryGetProperty("canCreate", out var cc) && cc.GetBoolean());
    }

    public async Task CreateAnnotationAsync(string annotationsUrl, int pageIndex, double x, double y, string text, string color, CancellationToken cancellationToken = default)
        => await CreateAnnotationAsync(annotationsUrl, pageIndex, 0, x, y, null, null, text, color, cancellationToken: cancellationToken);

    // Create a note (kind 0) or a markup shape (kind 1/2/3 with width/height; 4/5/6 stamp/strike/text-box; 7
    // freehand with points) — ADRs "Annotation markup" / 0525.
    public async Task CreateAnnotationAsync(string annotationsUrl, int pageIndex, int kind, double x, double y, double? width, double? height, string text, string color, string? points = null, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PostAsJsonAsync(annotationsUrl.TrimStart('/'), new { pageIndex, kind, positionX = x, positionY = y, width, height, text, color, points }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException("Could not add the markup.");
        }
    }

    public async Task UpdateAnnotationAsync(string annotationsUrl, Guid id, int pageIndex, double x, double y, string text, string color, string etag, CancellationToken cancellationToken = default)
        => await UpdateAnnotationAsync(annotationsUrl, id, pageIndex, x, y, null, null, text, color, etag, cancellationToken);

    public async Task UpdateAnnotationAsync(string annotationsUrl, Guid id, int pageIndex, double x, double y, double? width, double? height, string text, string color, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{annotationsUrl.TrimStart('/')}/{id}")
        {
            Content = JsonContent.Create(new { pageIndex, positionX = x, positionY = y, width, height, text, color }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        using var response = await _core.Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException("Could not save the note.");
        }
    }

    public async Task DeleteAnnotationAsync(string annotationsUrl, Guid id, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{annotationsUrl.TrimStart('/')}/{id}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        using var response = await _core.Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException("Could not delete the note.");
        }
    }
}
