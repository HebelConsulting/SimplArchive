using System.Net.Http.Headers;
using System.Text;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Audit;

// POSTs a single signed audit event to a tenant's SIEM webhook (ADR "Audit webhook streaming"). The HMAC-SHA256
// signature (computed by the dispatcher) rides in X-SimplArchive-Signature so the receiver can verify
// authenticity + integrity. Any non-2xx or transport error returns false, so the dispatcher retries next sweep.
public sealed class HttpAuditWebhookSender : IAuditWebhookSender
{
    public const string SignatureHeader = "X-SimplArchive-Signature";

    private readonly HttpClient _http;

    public HttpAuditWebhookSender(HttpClient http) => _http = http;

    public async Task<WebhookSendResult> SendAsync(string url, string jsonBody, string signature, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(SignatureHeader, $"sha256={signature}");

            using var response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? WebhookSendResult.Ok
                : WebhookSendResult.Fail($"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested is false)
        {
            // Unreachable / timeout / DNS / refused by the outbound-address policy — recorded + retried with
            // backoff. The INNERMOST message, because the handler wraps everything it raises in a generic
            // "An error occurred while sending the request", and that is what the administrator reads in
            // tenant settings as AuditWebhookLastError. A cause they cannot see is a cause they cannot fix.
            return WebhookSendResult.Fail(Innermost(ex).Message);
        }
    }

    private static Exception Innermost(Exception exception) =>
        exception.InnerException is { } inner ? Innermost(inner) : exception;
}
