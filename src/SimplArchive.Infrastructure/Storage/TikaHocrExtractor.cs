using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Search;

namespace SimplArchive.Infrastructure.Storage;

// OCRs a raster image into per-word boxes using Tesseract's hOCR output via the Tika sidecar: PUT the bytes,
// Accept text/html, and ask for hOCR (X-Tika-OCRoutputType) so the response carries `ocr_page` (page pixel
// size) and `ocrx_word` (per-word bbox) spans. Coordinates are normalized 0..1 within each page. Best-effort:
// any failure yields null (no overlay). See ADR "Search hit overlay (text layout)".
public sealed partial class TikaHocrExtractor : IImageTextLayoutExtractor
{
    private readonly HttpClient _http;
    private readonly ILogger<TikaHocrExtractor> _logger;
    private readonly string _ocrLanguages;

    public TikaHocrExtractor(HttpClient http, TikaOptions options, ILogger<TikaHocrExtractor> logger)
    {
        _http = http;
        _logger = logger;
        _ocrLanguages = options.OcrLanguages;
    }

    // A page-open (giving the page's pixel size) or a word (giving its bbox + text), matched in document order.
    [GeneratedRegex(
        @"class=['""]ocr_page['""][^>]*?bbox\s+\d+\s+\d+\s+(?<pw>\d+)\s+(?<ph>\d+)" +
        @"|class=['""]ocrx_word['""][^>]*?bbox\s+(?<x0>\d+)\s+(?<y0>\d+)\s+(?<x1>\d+)\s+(?<y1>\d+)[^>]*>(?<t>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HocrToken();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    public async Task<DocumentTextLayout?> ExtractAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "tika")
            {
                Content = new ByteArrayContent(imageBytes),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Accept.ParseAdd("text/html");
            request.Headers.Add("X-Tika-OCRoutputType", "hocr");
            request.Headers.Add("X-Tika-OCRLanguage", _ocrLanguages);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(html);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Tika hOCR extraction failed; no overlay will be offered.");
            return null;
        }
    }

    private static DocumentTextLayout? Parse(string html)
    {
        var pages = new List<TextLayoutPage>();
        List<TextLayoutWord>? current = null;
        double pageWidth = 0, pageHeight = 0;

        foreach (Match m in HocrToken().Matches(html))
        {
            if (m.Groups["pw"].Success)
            {
                current = [];
                pages.Add(new TextLayoutPage(current));
                pageWidth = double.Parse(m.Groups["pw"].Value);
                pageHeight = double.Parse(m.Groups["ph"].Value);
                continue;
            }

            if (current is null || pageWidth <= 0 || pageHeight <= 0)
            {
                continue; // a word before any page-open — skip
            }

            // Trimmed BEFORE the empty check, so a token that is only punctuation drops out entirely rather
            // than becoming a clickable box that copies nothing (#788).
            var text = TextLayoutValue.Trim(WebUtility.HtmlDecode(Tags().Replace(m.Groups["t"].Value, "")));
            if (text.Length == 0)
            {
                continue;
            }

            double x0 = double.Parse(m.Groups["x0"].Value), y0 = double.Parse(m.Groups["y0"].Value);
            double x1 = double.Parse(m.Groups["x1"].Value), y1 = double.Parse(m.Groups["y1"].Value);
            current.Add(new TextLayoutWord(
                text,
                X: x0 / pageWidth,
                Y: y0 / pageHeight,
                Width: (x1 - x0) / pageWidth,
                Height: (y1 - y0) / pageHeight));
        }

        return pages.Count > 0 ? new DocumentTextLayout(pages) : null;
    }
}

// No-op when Tika isn't configured — images then get no overlay (PDFs still do, via PdfPig).
public sealed class NullImageTextLayoutExtractor : IImageTextLayoutExtractor
{
    public Task<DocumentTextLayout?> ExtractAsync(byte[] imageBytes, CancellationToken cancellationToken = default) =>
        Task.FromResult<DocumentTextLayout?>(null);
}
