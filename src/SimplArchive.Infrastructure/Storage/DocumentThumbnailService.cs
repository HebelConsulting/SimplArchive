using System.Net.Http.Headers;
using NetVips;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// Draws a document's first page by POSTing its PDF to the OCR sidecar, and caches the PNG beside the version's
/// other derived artifacts (issue #476).
/// </summary>
/// <remarks>
/// The sidecar does the rasterising because the Api runs on Alpine (musl) and every usable PDF rasteriser ships
/// glibc-only natives — Docnet/PDFium has no musl build, and the NetVips musl bundle carries no PDF loader. That
/// image is Debian-based and already has ghostscript, so the capability was there for the asking.
///
/// The source is the DISPLAY object key, not the stored one: for an office document or an email that is the
/// converted PDF, and for a TIFF its converted PNG — what a reader would actually be shown. A PDF goes to the
/// sidecar; an image is shrunk in-process by libvips, which needs no network hop and works with no sidecar
/// configured at all. Anything with neither form — a .bin, a .zip — simply has no thumbnail, and the landing
/// page renders exactly as it did before this existed.
/// </remarks>
public sealed class DocumentThumbnailService : IDocumentThumbnailService
{
    // 300px wide, displayed at ~150 on the landing page: crisp on a retina screen, ~2-20 KB, and — the part
    // that matters for a page anyone with the link can open — body text is unreadable at this size, so the
    // picture identifies the document without handing over its contents. Headings do survive, but the page
    // already prints the document's NAME in its heading, so that is not new disclosure.
    private const int ThumbnailWidth = 300;

    // The leading dot is part of the suffix — DerivedKey concatenates it onto the stem verbatim, so omitting it
    // yields "contentthumb.png". Same shape as the text-layout sidecar's ".textlayout.json".
    private const string ThumbnailSuffix = ".thumb.png";

    private readonly HttpClient _http;
    private readonly IObjectStorageClient _objectStorage;
    private readonly IDocumentPreviewService _preview;
    private readonly ILogger<DocumentThumbnailService> _logger;

    public DocumentThumbnailService(
        HttpClient http,
        IObjectStorageClient objectStorage,
        IDocumentPreviewService preview,
        ILogger<DocumentThumbnailService> logger)
    {
        _http = http;
        _objectStorage = objectStorage;
        _preview = preview;
        _logger = logger;
    }

    public async Task<DocumentThumbnail?> EnsureThumbnailAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var thumbnailKey = ObjectKeyBuilder.DerivedKey(objectKey, ThumbnailSuffix);

        try
        {
            // The page count is NOT recoverable from a cached PNG, so a cache hit returns null for it and the
            // caller keeps whatever it stored the first time. Redrawing purely to re-read a number would spend
            // a sidecar round trip on something already in the database.
            if (await _objectStorage.ExistsAsync(thumbnailKey, cancellationToken))
            {
                return new DocumentThumbnail(thumbnailKey, null);
            }

            var displayKey = await _preview.GetDisplayObjectKeyAsync(objectKey, cancellationToken);

            // An image is already its own preview, so it would be perverse for it to be the one thing with no
            // thumbnail. libvips shrinks it here rather than the sidecar drawing it: NetVips is in this process
            // WITH musl natives (it is what converts TIFFs), so this branch needs no network hop at all. A TIFF
            // arrives here as its converted PNG, which is why this covers multi-page scans too.
            if (IsImage(displayKey))
            {
                return await ShrinkImageAsync(objectKey, displayKey, thumbnailKey, cancellationToken);
            }

            if (!displayKey.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // Anything with no PDF and no image form — a .bin, a .zip, an unknown binary. No thumbnail is
                // the honest answer, and the landing page renders exactly as it did before this existed.
                return null;
            }

            if (_http.BaseAddress is null)
            {
                // No OCR sidecar configured, so no PDF rasteriser — but this check belongs HERE, not at the top
                // of the method: the image branch above shrinks in-process and must keep working without one.
                return null;
            }

            byte[] pdf;
            await using (var source = await _objectStorage.GetObjectAsync(displayKey, cancellationToken))
            using (var buffer = new MemoryStream())
            {
                await source.CopyToAsync(buffer, cancellationToken);
                pdf = buffer.ToArray();
            }

            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(pdf);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(file, "file", "in.pdf");

            using var response = await _http.PostAsync($"thumbnail?width={ThumbnailWidth}", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("The OCR sidecar returned {Status} drawing a thumbnail for {ObjectKey}.", (int)response.StatusCode, objectKey);
                return null;
            }

            var png = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            using (var upload = new MemoryStream(png))
            {
                await _objectStorage.PutObjectAsync(thumbnailKey, upload, "image/png", cancellationToken);
            }

            // 0 means the sidecar could not read the count — a badge is an extra, so that becomes "unknown"
            // rather than a failure.
            var pageCount = response.Headers.TryGetValues("X-Page-Count", out var values)
                && int.TryParse(values.FirstOrDefault(), out var parsed) && parsed > 0
                    ? parsed
                    : (int?)null;

            return new DocumentThumbnail(thumbnailKey, pageCount);
        }
        catch (Exception e)
        {
            // Best-effort throughout: a share must not fail because a picture could not be drawn.
            _logger.LogWarning(e, "Could not produce a thumbnail for {ObjectKey}.", objectKey);
            return null;
        }
    }

    // The formats a browser renders as-is, which is exactly what GetDisplayObjectKeyAsync leaves untouched.
    private static bool IsImage(string key) =>
        Path.GetExtension(key).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private async Task<DocumentThumbnail?> ShrinkImageAsync(
        string objectKey, string displayKey, string thumbnailKey, CancellationToken cancellationToken)
    {
        byte[] original;
        await using (var source = await _objectStorage.GetObjectAsync(displayKey, cancellationToken))
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
            original = buffer.ToArray();
        }

        // ThumbnailBuffer, not a plain resize: libvips picks a shrink-on-load path, so a 40-megapixel photo is
        // never fully decoded just to produce a 300px picture.
        using var thumbnail = Image.ThumbnailBuffer(original, ThumbnailWidth);
        var png = thumbnail.PngsaveBuffer();

        using (var upload = new MemoryStream(png))
        {
            await _objectStorage.PutObjectAsync(thumbnailKey, upload, "image/png", cancellationToken);
        }

        // "Pages" for an image means the pages of the SOURCE — one for a photo, N for a multi-page scan, which
        // arrives here as N single-page renditions. Counting the display keys is exact and needs no decode.
        var pages = await _preview.GetDisplayObjectKeysAsync(objectKey, cancellationToken);
        return new DocumentThumbnail(thumbnailKey, Math.Max(pages.Count, 1));
    }

    public async Task<Uri?> GetThumbnailUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        var thumbnailKey = ObjectKeyBuilder.DerivedKey(objectKey, ThumbnailSuffix);
        try
        {
            return await _objectStorage.ExistsAsync(thumbnailKey, cancellationToken)
                ? await _objectStorage.GetPresignedPreviewUrlAsync(thumbnailKey, expiry, "thumbnail.png", "image/png", cancellationToken)
                : null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not resolve the cached thumbnail for {ObjectKey}.", objectKey);
            return null;
        }
    }
}
