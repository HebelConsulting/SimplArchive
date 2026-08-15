using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Conversion;

// Converts a TIFF to a searchable PDF by POSTing it to the OCR sidecar (OCRmyPDF/Tesseract), a typed
// HttpClient pointed at Ocr:Url (ADR "Searchable PDF successor for TIFFs"). Best-effort: any failure (sidecar
// down, OCR error) yields null, so the worker retries and the original TIFF version stays as-is.
public sealed class OcrmypdfConverter : ISearchablePdfConverter
{
    private readonly HttpClient _http;
    private readonly ILogger<OcrmypdfConverter> _logger;

    public OcrmypdfConverter(HttpClient http, ILogger<OcrmypdfConverter> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<byte[]?> ConvertToSearchablePdfAsync(byte[] sourceBytes, SearchablePdfSourceKind kind, string languages, bool deskew = false, bool rotate = false, CancellationToken cancellationToken = default)
    {
        if (_http.BaseAddress is null)
        {
            return null; // Ocr:Url not configured
        }

        var (contentType, fileName, kindParam) = kind switch
        {
            SearchablePdfSourceKind.Pdf => ("application/pdf", "in.pdf", "pdf"),
            _ => ("image/tiff", "in.tif", "tiff"),
        };

        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(sourceBytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(file, "file", fileName);

            var url = $"ocr?lang={Uri.EscapeDataString(languages)}&kind={kindParam}{(deskew ? "&deskew=true" : string.Empty)}{(rotate ? "&rotate=true" : string.Empty)}";
            using var response = await _http.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OCR sidecar returned {Status} converting a {Kind} to a searchable PDF.", (int)response.StatusCode, kind);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "OCR sidecar call failed; no searchable-PDF successor will be produced this attempt.");
            return null;
        }
    }
}

// No-op when Ocr:Url isn't configured — the searchable-PDF workflow is simply disabled.
public sealed class NullSearchablePdfConverter : ISearchablePdfConverter
{
    public Task<byte[]?> ConvertToSearchablePdfAsync(byte[] sourceBytes, SearchablePdfSourceKind kind, string languages, bool deskew = false, bool rotate = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);
}
