using System.Net.Http.Headers;
using System.Text;
using Markdig;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// Renders Markdown to PDF for preview — see ADR "CSV and Markdown preview". Converts the Markdown to HTML
// (Markdig), wraps it in a styled document, and renders it to PDF via Gotenberg's Chromium HTML route (the
// same route the email preview uses). Uses a typed HttpClient pointed at Gotenberg (set in AddInfrastructure);
// throws if Gotenberg isn't configured or the call fails, and the RenditionService then offers no preview.
public class MarkdownConverter : IMarkdownConverter
{
    private const string ChromiumHtmlRoute = "forms/chromium/convert/html";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly HttpClient _httpClient;

    public MarkdownConverter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<byte[]> ConvertToPdfAsync(byte[] markdownBytes, CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress is null)
        {
            throw new InvalidOperationException("Gotenberg is not configured (no Gotenberg:Url).");
        }

        var markdown = Encoding.UTF8.GetString(markdownBytes);
        var bodyHtml = Markdown.ToHtml(markdown, Pipeline);
        var html = BuildHtml(bodyHtml);

        using var form = new MultipartFormDataContent();
        var htmlContent = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        htmlContent.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        form.Add(htmlContent, "files", "index.html");

        using var response = await _httpClient.PostAsync(ChromiumHtmlRoute, form, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // Wraps the rendered Markdown in a full document with basic GitHub-ish styling. The strict CSP blocks all
    // external resource loading (same rationale as the email preview, ADR 0230): Markdown can embed remote
    // images, and Gotenberg's Chromium would otherwise wait on those fetches — this keeps the render fast and
    // prevents remote content from loading. Inline styles and data: images still render.
    private static string BuildHtml(string bodyHtml)
    {
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:;">
            <style>
              body { font-family: Arial, Helvetica, sans-serif; margin: 32px; color: #111; line-height: 1.5; }
              h1, h2, h3 { line-height: 1.25; }
              code, pre { font-family: "Courier New", monospace; background: #f4f4f4; }
              pre { padding: 10px; overflow: auto; }
              code { padding: 1px 4px; }
              pre code { padding: 0; }
              table { border-collapse: collapse; }
              th, td { border: 1px solid #ccc; padding: 4px 8px; }
              blockquote { margin: 0; padding-left: 12px; border-left: 3px solid #ccc; color: #555; }
              img { max-width: 100%; }
            </style></head><body>
            {{bodyHtml}}
            </body></html>
            """;
    }
}
