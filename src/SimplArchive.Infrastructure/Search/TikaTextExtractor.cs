using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Search;

// Configurable OCR settings (ADR "OCR for scanned documents") — the Tesseract language string (e.g.
// "eng+deu+fra+ita"), bound from the Tika:OcrLanguages configuration key in AddInfrastructure.
public sealed record TikaOptions(string OcrLanguages);

// Extracts plain text from document bytes via an Apache Tika sidecar (PUT the bytes to /tika, Accept
// text/plain). Tika auto-detects the format. OCR (ADR "OCR for scanned documents") is enabled via request
// headers: Tesseract (bundled in the tika:*-full image) OCRs image files automatically, and the PDF parser
// OCRs image-only pages under the `auto` strategy — in the configured languages. The headers are harmless
// against a non-full Tika image (no Tesseract → the OCR parser is a graceful no-op). Best-effort: any failure
// (unsupported format, Tika down) yields "" so indexing falls back to metadata-only. See ADR "OpenSearch
// full-text slice 1".
public sealed class TikaTextExtractor : ITextExtractor
{
    // Tika Server config-by-header: PDF parser properties are prefixed X-Tika-PDF, Tesseract OCR config
    // X-Tika-OCR. `auto` OCRs a PDF page only when it looks image-based / has little text, so normal text PDFs
    // aren't needlessly (and expensively) OCR'd.
    private const string PdfOcrStrategyHeader = "X-Tika-PDFOcrStrategy";
    private const string OcrLanguageHeader = "X-Tika-OCRLanguage";

    private readonly HttpClient _http;
    private readonly ILogger<TikaTextExtractor> _logger;
    private readonly string _ocrLanguages;

    public TikaTextExtractor(HttpClient http, TikaOptions options, ILogger<TikaTextExtractor> logger)
    {
        _http = http;
        _logger = logger;
        _ocrLanguages = options.OcrLanguages;
    }

    public async Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Put, "tika")
            {
                Content = new ByteArrayContent(buffer.ToArray()),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Accept.ParseAdd("text/plain");
            request.Headers.Add(PdfOcrStrategyHeader, "auto");
            request.Headers.Add(OcrLanguageHeader, _ocrLanguages);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return "";
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Tika text extraction failed; indexing content-less.");
            return "";
        }
    }
}

// No-op extractor — registered when Tika isn't configured, so indexing degrades to metadata-only.
public sealed class NullTextExtractor : ITextExtractor
{
    public Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default) => Task.FromResult("");
}
