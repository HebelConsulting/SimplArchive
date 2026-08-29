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

    /// <summary>The verbs whose MEANING is carried in an XML body rather than in headers.</summary>
    /// <remarks>
    /// A <c>PUT</c> body is the user's document and is never logged. A <c>PROPFIND</c> body is the list of
    /// property NAMES the client is asking for, and the answer is a list of names and values — metadata the
    /// path already reveals. So the whitelist here is by VERB, and it stays a whitelist: a verb is traceable
    /// because it was named, never because it was not excluded.
    /// </remarks>
    private static readonly string[] XmlBodyMethods = ["PROPFIND", "PROPPATCH", "LOCK"];

    internal static bool TracesBody(ILogger logger, string method) =>
        logger.IsEnabled(LogLevel.Trace) && XmlBodyMethods.Contains(method, StringComparer.Ordinal);

    /// <summary>What the client ASKED FOR — the half of a PROPFIND that headers cannot show.</summary>
    /// <remarks>
    /// Worth its own line because the answer MIRRORS it (issue #801, ADR 0713): the requested properties come
    /// back in the <c>200</c> propstat and the ones we lack in a <c>404</c> propstat naming them (RFC 4918
    /// §9.1), so which question produced which partition is exactly the sort of interop question that must be
    /// answerable from a log rather than from a packet capture (ADR 0626).
    /// </remarks>
    internal static void RequestBody(ILogger logger, string method, string path, string body)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
        {
            return;
        }

        logger.LogTrace("WebDAV → {Method} {Path} asked: {Body}",
            method, path, body.Length == 0 ? "(empty — treat as allprop)" : body);
    }

    /// <summary>What we ANSWERED — for the XML verbs, the answer IS the body.</summary>
    /// <remarks>
    /// The status line is not the answer to a PROPFIND, and treating it as one hid a real defect for months:
    /// every special folder reported <c>getlastmodified</c> as the UNIX EPOCH while returning a perfectly
    /// healthy <c>207</c>, so a status-only trace — and a suite of status-only assertions — showed a working
    /// server talking to a client that could not use it (#794).
    /// </remarks>
    internal static void ResponseBody(ILogger logger, HttpContext context, string body)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
        {
            return;
        }

        logger.LogTrace("WebDAV ← {Status} for {Method} {Path} answered: {Body}",
            context.Response.StatusCode, context.Request.Method, context.Request.Path, body);
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
