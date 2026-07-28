using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// Renders an uploaded .html/.htm file to PDF for preview — see ADR "HTML file preview". Injects a strict
// Content-Security-Policy into the document (blocking all external resource loading), then renders it to PDF
// via Gotenberg's Chromium HTML route (server-side, so the file's scripts run in Gotenberg's sandbox, never
// the user's browser). Uses a typed HttpClient pointed at Gotenberg (set in AddInfrastructure); throws if
// Gotenberg isn't configured or the call fails, and the RenditionService then offers no preview.
public partial class HtmlConverter : IHtmlConverter
{
    private const string ChromiumHtmlRoute = "forms/chromium/convert/html";

    // Same policy as the email/markdown previews: no external loads at all (fast render, no remote content /
    // tracking / SSRF), inline styles and data: images still render.
    private const string CspMeta =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:;\">";

    private readonly HttpClient _httpClient;

    public HtmlConverter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<byte[]> ConvertToPdfAsync(byte[] htmlBytes, CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress is null)
        {
            throw new InvalidOperationException("Gotenberg is not configured (no Gotenberg:Url).");
        }

        var html = InjectCsp(Encoding.UTF8.GetString(htmlBytes));

        using var form = new MultipartFormDataContent();
        var htmlContent = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        htmlContent.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        form.Add(htmlContent, "files", "index.html");

        using var response = await _httpClient.PostAsync(ChromiumHtmlRoute, form, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // Inserts the CSP meta as early in the document as possible so it governs all resources: right after the
    // opening <head> if present, else after <html> (wrapped in a head), else prepended.
    private static string InjectCsp(string html)
    {
        var head = HeadTag().Match(html);
        if (head.Success)
        {
            return html.Insert(head.Index + head.Length, CspMeta);
        }

        var htmlTag = HtmlTag().Match(html);
        if (htmlTag.Success)
        {
            return html.Insert(htmlTag.Index + htmlTag.Length, $"<head>{CspMeta}</head>");
        }

        return $"<head>{CspMeta}</head>{html}";
    }

    [GeneratedRegex("<head[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadTag();

    [GeneratedRegex("<html[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTag();
}
