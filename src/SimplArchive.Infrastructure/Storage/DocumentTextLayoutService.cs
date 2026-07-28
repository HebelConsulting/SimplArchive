using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// Produces per-page word boxes for hit-overlay (ADR "Search hit overlay (text layout)"). Runs against the
// object the client actually displays (the preview rendition when one applies, else the original — via
// IDocumentPreviewService.GetDisplayObjectKeyAsync), so boxes line up with what's shown: image bytes go
// through OCR (Tesseract hOCR), PDF bytes through their text layer (PdfPig). The computed layout is cached as
// a sidecar object ("<dir>/<stem>.textlayout.json"); subsequent requests reuse it. Any failure or an
// unsupported display format yields null (the caller omits the overlay link).
public sealed class DocumentTextLayoutService : IDocumentTextLayoutService
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IDocumentPreviewService _previewService;
    private readonly IImageTextLayoutExtractor _imageExtractor;
    private readonly ILogger<DocumentTextLayoutService> _logger;

    public DocumentTextLayoutService(
        IObjectStorageClient objectStorageClient,
        IDocumentPreviewService previewService,
        IImageTextLayoutExtractor imageExtractor,
        ILogger<DocumentTextLayoutService> logger)
    {
        _objectStorageClient = objectStorageClient;
        _previewService = previewService;
        _imageExtractor = imageExtractor;
        _logger = logger;
    }

    public async Task<DocumentTextLayout?> GetTextLayoutAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var sidecarKey = SidecarKey(objectKey);

        try
        {
            if (await _objectStorageClient.ExistsAsync(sidecarKey, cancellationToken))
            {
                await using var cached = await _objectStorageClient.GetObjectAsync(sidecarKey, cancellationToken);
                return await JsonSerializer.DeserializeAsync<DocumentTextLayout>(cached, Json, cancellationToken);
            }

            // The bytes the client displays, one key per preview page (N for a multi-page TIFF, else one) —
            // coordinates must be relative to these, and page order must match the preview's. See ADR
            // "Multi-page TIFF preview pages".
            var displayKeys = await _previewService.GetDisplayObjectKeysAsync(objectKey, cancellationToken);

            var pages = new List<TextLayoutPage>();
            foreach (var displayKey in displayKeys)
            {
                var displayExtension = Path.GetExtension(displayKey);
                var isImage = ImageExtensions.Contains(displayExtension);
                var isPdf = displayExtension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                if (!isImage && !isPdf)
                {
                    return null; // text/other display formats have no overlay
                }

                var displayBytes = await ReadBytesAsync(displayKey, cancellationToken);
                var partial = isImage
                    ? await _imageExtractor.ExtractAsync(displayBytes, cancellationToken)
                    : PdfTextLayoutReader.Read(displayBytes);

                // An image page that OCRs to nothing still holds a page slot, so overlay page indices stay
                // aligned with the preview's pages; a PDF contributes all its own pages.
                if (partial is null)
                {
                    pages.Add(new TextLayoutPage([]));
                }
                else
                {
                    pages.AddRange(partial.Pages);
                }
            }

            if (pages.Count == 0 || pages.All(p => p.Words.Count == 0))
            {
                return null; // nothing recognized anywhere — don't cache, so a later retry can succeed
            }

            var layout = new DocumentTextLayout(pages);
            using var payload = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(layout, Json));
            await _objectStorageClient.PutObjectAsync(sidecarKey, payload, "application/json", cancellationToken);
            return layout;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to produce a text layout for {ObjectKey}; no overlay will be offered.", objectKey);
            return null;
        }
    }

    private async Task<byte[]> ReadBytesAsync(string objectKey, CancellationToken cancellationToken)
    {
        await using var source = await _objectStorageClient.GetObjectAsync(objectKey, cancellationToken);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // "<dir>/<stem>.textlayout.json" — same directory as the original, its extension replaced (mirrors the
    // rendition key scheme, so all derived artefacts sit alongside the object).
    private static string SidecarKey(string objectKey)
    {
        var lastSlash = objectKey.LastIndexOf('/');
        var directory = lastSlash >= 0 ? objectKey[..(lastSlash + 1)] : string.Empty;
        var fileName = lastSlash >= 0 ? objectKey[(lastSlash + 1)..] : objectKey;
        var lastDot = fileName.LastIndexOf('.');
        var stem = lastDot >= 0 ? fileName[..lastDot] : fileName;
        return $"{directory}{stem}.textlayout.json";
    }
}
