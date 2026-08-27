using System.Text;

namespace SimplArchive.Api.WebDav;

/// <summary>
/// The WebDAV seam, answerable at <c>Trace</c> (ADR 0626).
/// </summary>
/// <remarks>
/// <para>
/// The ADR names WebDAV among the protocol endpoints whose <b>complete exchange must be recoverable at
/// Trace</b> — and it was the one that never had it. The cost was paid in full on #762: five rounds of
/// diagnosis from status codes alone, because the request log shows <i>which</i> verb and <i>what</i> code but
/// not the <c>If</c>, <c>Lock-Token</c>, <c>Destination</c> or <c>Depth</c> headers that decide how a client
/// behaves. "What exactly passed between us?" could not be answered without a packet capture, which is the
/// definition the ADR gives of an unfinished seam.
/// </para>
/// <para>
/// <b>Redaction is a whitelist, never a hunt.</b> Headers are logged only if named here, so an
/// <c>Authorization</c> or <c>Cookie</c> cannot leak by being forgotten — adding a header to the log is a
/// deliberate act. Bodies are never logged: a document's first bytes are its content, and the same reasoning
/// that governs IMAP <c>APPEND</c> applies to a WebDAV <c>PUT</c>. Their PRESENCE and LENGTH are recorded,
/// which is what a protocol argument actually turns on.
/// </para>
/// </remarks>
internal static class WebDavTrace
{
    // What a client's behaviour is decided by. Everything else is either noise or a credential.
    private static readonly string[] RequestHeaders =
    [
        "Depth", "Destination", "Overwrite", "If", "Lock-Token", "Timeout",
        "Content-Type", "Content-Length", "User-Agent", "X-Expected-Entity-Length",
    ];

    private static readonly string[] ResponseHeaders =
    [
        "DAV", "Allow", "Lock-Token", "ETag", "Content-Type", "Content-Length", "MS-Author-Via",
    ];

    internal static void Request(ILogger logger, HttpContext context, string method, string path)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
        {
            return;
        }

        logger.LogTrace("WebDAV → {Method} {Path} [{Headers}] body={BodyBytes}",
            method, path, Format(context.Request.Headers, RequestHeaders),
            context.Request.ContentLength is { } length ? $"{length}B" : "none");
    }

    internal static void Response(ILogger logger, HttpContext context, string method, string path)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
        {
            return;
        }

        logger.LogTrace("WebDAV ← {Status} for {Method} {Path} [{Headers}]",
            context.Response.StatusCode, method, path, Format(context.Response.Headers, ResponseHeaders));
    }

    private static string Format(IHeaderDictionary headers, string[] whitelist)
    {
        var parts = new StringBuilder();
        foreach (var name in whitelist)
        {
            if (headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value.ToString()))
            {
                if (parts.Length > 0)
                {
                    parts.Append("; ");
                }

                parts.Append(name).Append(": ").Append(value.ToString());
            }
        }

        return parts.Length == 0 ? "-" : parts.ToString();
    }
}
