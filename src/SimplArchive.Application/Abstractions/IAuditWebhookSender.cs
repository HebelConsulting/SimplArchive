namespace SimplArchive.Application.Abstractions;

/// <summary>
/// The outcome of a single webhook POST (ADR "Audit webhook delivery retry/backoff"). On failure, Error carries a
/// short reason (e.g. "HTTP 503" or a transport message) that the dispatcher records for the health surface.
/// </summary>
public readonly record struct WebhookSendResult(bool Success, string? Error)
{
    public static readonly WebhookSendResult Ok = new(true, null);

    public static WebhookSendResult Fail(string error) => new(false, error);
}

/// <summary>
/// Sends a single audit event to a tenant's configured SIEM webhook (ADR "Audit webhook streaming"). The
/// dispatcher computes the HMAC-SHA256 signature (it holds the secret) and passes it here; the sender just POSTs
/// the JSON body with the signature header. Returns a successful result on a 2xx response, otherwise a failure
/// carrying the reason (so the dispatcher records it, backs off, and retries the same event later — at-least-once).
/// </summary>
public interface IAuditWebhookSender
{
    Task<WebhookSendResult> SendAsync(string url, string jsonBody, string signature, CancellationToken cancellationToken = default);
}
