using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NetVips;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// See ADR "Server-side preview renditions for non-browser-viewable images" and "Office document preview via
// Gotenberg". For a format the browser can't display, this fetches the original, converts it to a
// browser-viewable rendition, and caches the result in object storage at "<same dir>/<guid>.preview.<ext>";
// subsequent previews reuse the cached rendition. Two families: raster images the browser can't show (TIFF)
// -> PNG via NetVips/libvips; office/OpenDocument files -> PDF via the Gotenberg sidecar. Browser-viewable
// formats bypass all of this and get the original's own inline URL.
public class RenditionService : IDocumentPreviewService
{
    private enum RenditionKind
    {
        None,
        ImageToPng,
        OfficeToPdf,
        EmailToPdf,
        MarkdownToPdf,
        HtmlToPdf,
        JsonPretty,
        XmlPretty,
    }

    // Raster formats browsers can't render natively, converted to PNG. Deliberately a small explicit set
    // (not "everything but the viewable ones") so we only convert formats libvips is known to decode.
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff" };

    // Office / OpenDocument formats, converted to PDF (rendered by the browser's built-in PDF viewer). CSV
    // rides this route too — LibreOffice opens it as a spreadsheet, so it previews as a table (ADR "CSV and
    // Markdown preview").
    private static readonly HashSet<string> OfficeExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".odt", ".ods", ".docx", ".xlsx", ".pptx", ".csv" };

    // Markdown, rendered to HTML then PDF so it previews formatted rather than as raw source (ADR "CSV and
    // Markdown preview").
    private static readonly HashSet<string> MarkdownExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown" };

    // HTML, rendered server-side to a static PDF (with a CSP blocking remote loads) rather than run live in
    // the browser (ADR "HTML file preview").
    private static readonly HashSet<string> HtmlExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    // Email formats, parsed and rendered to PDF (LibreOffice can't open these, so they take a dedicated
    // parse-then-Chromium path, not the office route). See ADR "Email (.eml/.msg) preview".
    private static readonly HashSet<string> EmailExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".eml", ".msg" };

    // Plain-text formats the browser can render directly (no rendition) — but only when served as text/plain.
    // We force that content-type on the preview URL so it renders inline regardless of the stored type (and
    // it prevents content-type sniffing of a .txt holding markup). See ADR "Plain-text inline preview".
    private static readonly HashSet<string> TextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt" };

    // JSON/XML are pretty-printed (re-indented) into a cached rendition served as text, so even minified
    // input reads well (ADR "JSON and XML preview"). XML can't use the browser's built-in tree viewer here:
    // Chrome only runs that for top-level navigations, not inside the preview iframe, where it flattens to
    // text content — so we show indented source instead.
    private static readonly HashSet<string> JsonExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".json" };

    private static readonly HashSet<string> XmlExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".xml" };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        // Keep unicode readable in the pretty output rather than \uXXXX-escaping it.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IOfficeConverter _officeConverter;
    private readonly IEmailConverter _emailConverter;
    private readonly IMarkdownConverter _markdownConverter;
    private readonly IHtmlConverter _htmlConverter;
    private readonly ILogger<RenditionService> _logger;

    public RenditionService(
        IObjectStorageClient objectStorageClient,
        IOfficeConverter officeConverter,
        IEmailConverter emailConverter,
        IMarkdownConverter markdownConverter,
        IHtmlConverter htmlConverter,
        ILogger<RenditionService> logger)
    {
        _objectStorageClient = objectStorageClient;
        _officeConverter = officeConverter;
        _emailConverter = emailConverter;
        _markdownConverter = markdownConverter;
        _htmlConverter = htmlConverter;
        _logger = logger;
    }

    public async Task<DocumentPreview?> GetPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default, bool sourceMayHaveChanged = false)
    {
        var extension = ExtensionFor(objectKey, fileName);
        var kind = KindFor(extension);
        if (kind == RenditionKind.None)
        {
            // No rendition needed: serve the original inline (it IS the original, so not "converted"),
            // forcing text/plain for .txt so it renders inline regardless of the stored type.
            var contentType = PreviewContentTypeOverride(extension);
            var originalUrl = await _objectStorageClient.GetPresignedPreviewUrlAsync(objectKey, expiry, fileName, contentType, cancellationToken);
            return new DocumentPreview(originalUrl, IsConverted: false);
        }

        var renditionKey = RenditionKey(objectKey, kind);

        try
        {
            // The cached sidecar is keyed on the SOURCE PATH, which is right for a document version (immutable
            // once confirmed) and wrong for a source that changes underneath a stable key — the check-out
            // working-copy stash is rewritten on every save over WebDAV. Reusing the sidecar there would serve
            // the PREVIOUS edit's rendition: a wrong document, shown confidently. So such a caller says so, and
            // the rendition is regenerated over the same key rather than accumulating a variant per edit.
            if (sourceMayHaveChanged || !await _objectStorageClient.ExistsAsync(renditionKey, cancellationToken))
            {
                await GenerateRenditionAsync(objectKey, renditionKey, kind, extension, cancellationToken);
            }

            var renditionUrl = await _objectStorageClient.GetPresignedPreviewUrlAsync(renditionKey, expiry, fileName, cancellationToken: cancellationToken);
            return new DocumentPreview(renditionUrl, IsConverted: true);
        }
        catch (Exception e)
        {
            // Falling back to the original is pointless here — the browser can't render the raw file (that's
            // why we convert it), so it would just show a blank pane. Return null instead; the caller omits
            // the preview link and the client shows "No preview available". The failure isn't cached (no
            // rendition object was written), so the next request retries — e.g. once Gotenberg is back up.
            // See ADR "Preview fallback when a rendition can't be produced".
            _logger.LogWarning(e, "Failed to produce preview rendition for {ObjectKey}; no preview will be offered.", objectKey);
            return null;
        }
    }

    // The object the client displays: the (on-demand-generated) rendition when the format needs one, else the
    // original. Lets the text-layout service run OCR / PDF text extraction against exactly what's shown, so
    // hit-overlay boxes line up. A rendition-conversion failure propagates (the caller treats it as no overlay).
    public async Task<string> GetDisplayObjectKeyAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var kind = KindFor(ExtensionFor(objectKey, null));
        if (kind == RenditionKind.None)
        {
            return objectKey; // displayed as-is (PDF, browser-viewable image, .txt)
        }

        var renditionKey = RenditionKey(objectKey, kind);
        if (!await _objectStorageClient.ExistsAsync(renditionKey, cancellationToken))
        {
            await GenerateRenditionAsync(objectKey, renditionKey, kind, ExtensionFor(objectKey, null), cancellationToken);
        }

        return renditionKey;
    }

    // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages"); null for every
    // other format (the caller then uses the single GetPreviewUrlAsync). A single-page TIFF also returns null
    // (it uses the ordinary single ".preview.png").
    public async Task<PreviewPages?> GetPreviewPagesAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (KindFor(ExtensionFor(objectKey, null)) != RenditionKind.ImageToPng)
        {
            return null;
        }

        try
        {
            var keys = await EnsureImagePageKeysAsync(objectKey, cancellationToken);
            if (keys.Count <= 1)
            {
                return null; // single page — the normal single preview covers it
            }

            var urls = new List<Uri>();
            foreach (var key in keys)
            {
                urls.Add(await _objectStorageClient.GetPresignedPreviewUrlAsync(key, expiry, fileName, cancellationToken: cancellationToken));
            }

            return new PreviewPages(urls, IsConverted: true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to produce multi-page preview for {ObjectKey}; falling back to the single preview.", objectKey);
            return null;
        }
    }

    // One display key per preview page — the per-page PNGs for a multi-page TIFF, else the single display key.
    public async Task<IReadOnlyList<string>> GetDisplayObjectKeysAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (KindFor(ExtensionFor(objectKey, null)) == RenditionKind.ImageToPng)
        {
            var keys = await EnsureImagePageKeysAsync(objectKey, cancellationToken);
            if (keys.Count > 1)
            {
                return keys;
            }
            // single-page TIFF falls through to the single display key
        }

        return [await GetDisplayObjectKeyAsync(objectKey, cancellationToken)];
    }

    // Ensures the per-page PNG renditions of a TIFF exist (generating on first use) and returns their keys in
    // page order. A single-page TIFF reuses the ordinary ".preview.png" (so the single-preview path stays
    // consistent); a multi-page TIFF gets "<stem>.preview.p{n}.png" per page. A tiny "<stem>.preview.pages"
    // manifest records the page count so subsequent calls skip re-downloading/decoding.
    private async Task<IReadOnlyList<string>> EnsureImagePageKeysAsync(string objectKey, CancellationToken cancellationToken)
    {
        var manifestKey = ImagePagesManifestKey(objectKey);
        if (await _objectStorageClient.ExistsAsync(manifestKey, cancellationToken))
        {
            var count = await ReadPageCountAsync(manifestKey, cancellationToken);
            return BuildImagePageKeys(objectKey, count);
        }

        var originalBytes = await DownloadBytesAsync(objectKey, cancellationToken);
        var pages = ConvertToPngPages(originalBytes);

        var keys = new List<string>();
        if (pages.Count <= 1)
        {
            var single = RenditionKey(objectKey, RenditionKind.ImageToPng);
            await PutBytesAsync(single, pages.Count == 1 ? pages[0] : ConvertToPng(originalBytes), "image/png", cancellationToken);
            keys.Add(single);
        }
        else
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var key = ImagePageKey(objectKey, i + 1);
                await PutBytesAsync(key, pages[i], "image/png", cancellationToken);
                keys.Add(key);
            }
        }

        await PutBytesAsync(manifestKey, Encoding.UTF8.GetBytes(keys.Count.ToString()), "text/plain", cancellationToken);
        return keys;
    }

    private static IReadOnlyList<string> BuildImagePageKeys(string objectKey, int count) =>
        count <= 1
            ? [RenditionKey(objectKey, RenditionKind.ImageToPng)]
            : Enumerable.Range(1, count).Select(i => ImagePageKey(objectKey, i)).ToList();

    private async Task<int> ReadPageCountAsync(string manifestKey, CancellationToken cancellationToken)
    {
        await using var stream = await _objectStorageClient.GetObjectAsync(manifestKey, cancellationToken);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return int.TryParse(text.Trim(), out var count) ? Math.Max(1, count) : 1;
    }

    private async Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken cancellationToken)
    {
        await using var source = await _objectStorageClient.GetObjectAsync(objectKey, cancellationToken);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private async Task PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes);
        await _objectStorageClient.PutObjectAsync(key, stream, contentType, cancellationToken);
    }

    // Renders each TIFF page to its own PNG (page order preserved), tolerating pages of differing sizes — the
    // case the single-image "stack all pages" path (ConvertToPng) can't handle. Loads the page count from the
    // first page's `n-pages` metadata, then decodes each page individually; a page that fails to decode is
    // skipped rather than failing the whole document. Falls back to the whole image if nothing decoded.
    private static List<byte[]> ConvertToPngPages(byte[] originalBytes)
    {
        var pages = new List<byte[]>();

        var pageCount = 1;
        try
        {
            using var first = Image.NewFromBuffer(originalBytes);
            pageCount = Math.Max(1, Convert.ToInt32(first.Get("n-pages")));
        }
        catch (Exception)
        {
            pageCount = 1; // no page-count metadata — treat as single page
        }

        for (var i = 0; i < pageCount; i++)
        {
            try
            {
                using var page = Image.NewFromBuffer(originalBytes, kwargs: new VOption { { "page", i }, { "n", 1 } });
                pages.Add(page.WriteToBuffer(".png"));
            }
            catch (VipsException)
            {
                // skip an undecodable page
            }
        }

        if (pages.Count == 0)
        {
            using var whole = Image.NewFromBuffer(originalBytes);
            pages.Add(whole.WriteToBuffer(".png"));
        }

        return pages;
    }

    private static string ImagePageKey(string objectKey, int page) => WithSuffix(objectKey, $".preview.p{page}.png");

    private static string ImagePagesManifestKey(string objectKey) => WithSuffix(objectKey, ".preview.pages");

    private async Task GenerateRenditionAsync(string objectKey, string renditionKey, RenditionKind kind, string extension, CancellationToken cancellationToken)
    {
        byte[] originalBytes;
        await using (var source = await _objectStorageClient.GetObjectAsync(objectKey, cancellationToken))
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
            originalBytes = buffer.ToArray();
        }

        var (renditionBytes, contentType) = kind switch
        {
            RenditionKind.ImageToPng => (ConvertToPng(originalBytes), "image/png"),
            RenditionKind.OfficeToPdf => (await _officeConverter.ConvertToPdfAsync(originalBytes, $"source{extension}", cancellationToken), "application/pdf"),
            RenditionKind.EmailToPdf => (await _emailConverter.ConvertToPdfAsync(originalBytes, extension, cancellationToken), "application/pdf"),
            RenditionKind.MarkdownToPdf => (await _markdownConverter.ConvertToPdfAsync(originalBytes, cancellationToken), "application/pdf"),
            RenditionKind.HtmlToPdf => (await _htmlConverter.ConvertToPdfAsync(originalBytes, cancellationToken), "application/pdf"),
            RenditionKind.JsonPretty => (PrettyPrintJson(originalBytes), "application/json; charset=utf-8"),
            RenditionKind.XmlPretty => (PrettyPrintXml(originalBytes), "text/plain; charset=utf-8"),
            _ => throw new InvalidOperationException($"Unexpected rendition kind {kind}."),
        };

        using var renditionStream = new MemoryStream(renditionBytes);
        await _objectStorageClient.PutObjectAsync(renditionKey, renditionStream, contentType, cancellationToken);
    }

    // Renders every page of a (possibly multi-page) TIFF into a single tall PNG — libvips loads all pages
    // with n=-1 as one "toilet-roll" image stacked vertically, so the workbench preview shows page 1 at the
    // top and you scroll down through the rest (continuous view). Pages with differing sizes can't be stacked
    // into one image (libvips throws), so we fall back to the first page only rather than a broken preview.
    private static byte[] ConvertToPng(byte[] originalBytes)
    {
        try
        {
            using var allPages = Image.NewFromBuffer(originalBytes, kwargs: new VOption { { "n", -1 } });
            return allPages.WriteToBuffer(".png");
        }
        catch (VipsException)
        {
            using var firstPage = Image.NewFromBuffer(originalBytes);
            return firstPage.WriteToBuffer(".png");
        }
    }

    // The extension that decides the format. Normally the object key carries it, but a key can legitimately be
    // extensionless — the check-out working-copy stash is (ADR 0517) — and then the caller's display file name is
    // the only thing that knows what the bytes are. Without this fallback an extensionless key resolves to
    // RenditionKind.None and the caller is handed a raw .docx as its "preview".
    private static string ExtensionFor(string objectKey, string? fileName) =>
        Path.GetExtension(objectKey) is { Length: > 0 } fromKey ? fromKey : Path.GetExtension(fileName ?? "");

    private static RenditionKind KindFor(string extension)
    {
        if (ImageExtensions.Contains(extension))
        {
            return RenditionKind.ImageToPng;
        }

        if (OfficeExtensions.Contains(extension))
        {
            return RenditionKind.OfficeToPdf;
        }

        if (EmailExtensions.Contains(extension))
        {
            return RenditionKind.EmailToPdf;
        }

        if (MarkdownExtensions.Contains(extension))
        {
            return RenditionKind.MarkdownToPdf;
        }

        if (HtmlExtensions.Contains(extension))
        {
            return RenditionKind.HtmlToPdf;
        }

        if (JsonExtensions.Contains(extension))
        {
            return RenditionKind.JsonPretty;
        }

        return XmlExtensions.Contains(extension) ? RenditionKind.XmlPretty : RenditionKind.None;
    }

    // Content-type to force on the preview of a format served as-is (no rendition) so it renders inline.
    private static string? PreviewContentTypeOverride(string extension)
        => TextExtensions.Contains(extension) ? "text/plain; charset=utf-8" : null;

    // Re-indents JSON so even minified input reads well. Invalid JSON is passed through unchanged so it still
    // previews (as its raw text) rather than failing the whole preview.
    private static byte[] PrettyPrintJson(byte[] originalBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(originalBytes);
            return JsonSerializer.SerializeToUtf8Bytes(document.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            return originalBytes;
        }
    }

    // Re-indents XML into readable source (shown as text, since the browser's tree viewer doesn't run in the
    // iframe). Invalid XML is passed through unchanged so it still previews rather than failing.
    private static byte[] PrettyPrintXml(byte[] originalBytes)
    {
        try
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(originalBytes));
            return Encoding.UTF8.GetBytes(document.ToString());
        }
        catch (XmlException)
        {
            return originalBytes;
        }
    }

    // "<dir>/<guid>.preview.<ext>" — same path as the original, its extension replaced by the suffix.
    private static string RenditionKey(string objectKey, RenditionKind kind) => WithSuffix(objectKey, kind switch
    {
        RenditionKind.ImageToPng => ".preview.png",
        RenditionKind.JsonPretty => ".preview.json",
        RenditionKind.XmlPretty => ".preview.txt",
        _ => ".preview.pdf",
    });

    // Replaces the object key's file extension with a derived suffix, keeping it in the same directory — the
    // scheme all derived artefacts (renditions, per-page images, text-layout) share (ObjectKeyBuilder, issue #338).
    private static string WithSuffix(string objectKey, string suffix) => ObjectKeyBuilder.DerivedKey(objectKey, suffix);
}
