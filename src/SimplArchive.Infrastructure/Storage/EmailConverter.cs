using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MimeKit;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// Converts .eml/.msg emails to PDF for preview — see ADR "Email (.eml/.msg) preview". Parses the message
// (MimeKit for .eml, MSGReader for .msg), builds a clean HTML view (envelope header + body), and renders it
// to PDF via Gotenberg's Chromium HTML route. Uses a typed HttpClient whose BaseAddress is the Gotenberg URL
// (set in AddInfrastructure); if Gotenberg isn't configured or the call fails, this throws and the
// RenditionService offers no preview.
public class EmailConverter : IEmailConverter
{
    // Gotenberg 8's Chromium HTML-to-PDF route; the main file must be named index.html.
    private const string ChromiumHtmlRoute = "forms/chromium/convert/html";

    private readonly HttpClient _httpClient;

    public EmailConverter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<byte[]> ConvertToPdfAsync(byte[] emailBytes, string extension, CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress is null)
        {
            throw new InvalidOperationException("Gotenberg is not configured (no Gotenberg:Url).");
        }

        var view = extension.Equals(".msg", StringComparison.OrdinalIgnoreCase)
            ? ParseMsg(emailBytes)
            : ParseEml(emailBytes);

        var html = BuildHtml(view);

        using var form = new MultipartFormDataContent();
        var htmlContent = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        htmlContent.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        form.Add(htmlContent, "files", "index.html");

        using var response = await _httpClient.PostAsync(ChromiumHtmlRoute, form, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static EmailView ParseEml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var message = MimeMessage.Load(stream);
        return new EmailView(
            message.Subject ?? string.Empty,
            message.From?.ToString() ?? string.Empty,
            message.To?.ToString() ?? string.Empty,
            message.Date == default ? string.Empty : message.Date.ToString("f"),
            message.HtmlBody,
            message.TextBody);
    }

    private static EmailView ParseMsg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var message = new MsgReader.Outlook.Storage.Message(stream);

        var to = string.Join(", ", message.Recipients
            .Where(r => r.Type == MsgReader.Outlook.RecipientType.To)
            .Select(r => FormatAddress(r.DisplayName, r.Email)));

        return new EmailView(
            message.Subject ?? string.Empty,
            FormatAddress(message.Sender?.DisplayName, message.Sender?.Email),
            to,
            message.SentOn?.ToString("f") ?? string.Empty,
            message.BodyHtml,
            message.BodyText);
    }

    private static string FormatAddress(string? displayName, string? email)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return email ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(email) ? displayName : $"{displayName} <{email}>";
    }

    // Envelope header block + the email body. The body's own HTML is embedded as-is (Gotenberg's Chromium
    // renders it server-side into a static PDF); header fields are HTML-encoded. Attachments and inline
    // (cid:) images are out of scope for this slice.
    private static string BuildHtml(EmailView view)
    {
        var body = !string.IsNullOrWhiteSpace(view.BodyHtml)
            ? view.BodyHtml
            : $"<pre style=\"white-space:pre-wrap;font-family:inherit\">{WebUtility.HtmlEncode(view.BodyText ?? string.Empty)}</pre>";

        // Block ALL external resource loading: Chromium then makes no network requests, so the render is
        // fast (no waiting on network-idle for remote logos) AND email tracking pixels / remote content
        // can't phone home when a message is previewed. Inline styles and data: images still render;
        // remote/cid images are dropped (attachments/inline images are out of scope for this slice).
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:;">
            <style>
              body { font-family: Arial, Helvetica, sans-serif; margin: 24px; color: #111; }
              .subject { font-size: 18px; font-weight: bold; margin-bottom: 10px; }
              .env { border-bottom: 1px solid #ccc; padding-bottom: 10px; margin-bottom: 16px; font-size: 13px; }
              .env div { margin: 2px 0; }
              .env .l { color: #666; display: inline-block; width: 60px; }
            </style></head><body>
              <div class="subject">{{WebUtility.HtmlEncode(view.Subject)}}</div>
              <div class="env">
                <div><span class="l">From:</span> {{WebUtility.HtmlEncode(view.From)}}</div>
                <div><span class="l">To:</span> {{WebUtility.HtmlEncode(view.To)}}</div>
                <div><span class="l">Date:</span> {{WebUtility.HtmlEncode(view.Date)}}</div>
              </div>
              <div class="body">{{body}}</div>
            </body></html>
            """;
    }

    private record EmailView(string Subject, string From, string To, string Date, string? BodyHtml, string? BodyText);
}
