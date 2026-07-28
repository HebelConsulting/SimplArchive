using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// Converts office documents to PDF via the Gotenberg sidecar's LibreOffice route — see ADR "Office document
// preview via Gotenberg". Uses a typed HttpClient whose BaseAddress is the Gotenberg URL (set in
// AddInfrastructure). If Gotenberg isn't configured (no BaseAddress) or the call fails, this throws and the
// RenditionService falls back to the original file.
public class GotenbergOfficeConverter : IOfficeConverter
{
    // Gotenberg 8's LibreOffice conversion route.
    private const string ConvertRoute = "forms/libreoffice/convert";

    private readonly HttpClient _httpClient;

    public GotenbergOfficeConverter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<byte[]> ConvertToPdfAsync(byte[] source, string fileName, CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress is null)
        {
            throw new InvalidOperationException("Gotenberg is not configured (no Gotenberg:Url).");
        }

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(source);
        // The multipart field must be named "files"; the filename's extension tells LibreOffice the format.
        form.Add(fileContent, "files", fileName);

        using var response = await _httpClient.PostAsync(ConvertRoute, form, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
