// PORTED from the sister project SimplCalCon (Apache-2.0, same licence) — see ADR 0621. This is the
// ready-made version of the wire tap hand-built for IMAP debugging on 2026-08-17: when a native client
// silently refuses to work, the only way to find out why is to see what it actually sent.
using System.Collections.Concurrent;

namespace SimplArchive.Api.CalDav;

/// <summary>
/// DAV observability for diagnosing native CalDAV/CardDAV clients (ported from SimplCalCon, ADR 0621). Two signals
/// over the <c>SimplArchive.Dav.Wire</c> category:
/// <list type="bullet">
/// <item><b>Verbose wire trace (Trace)</b> — the full <c>/dav</c> request/response bodies
/// (method, path, depth, status + raw XML/blob). The most verbose signal we emit ("may
/// clutter"), so it is <b>off by default</b> and gated on <c>IsEnabled(LogLevel.Trace)</c>;
/// when off the middleware is a pass-through with no body buffering. Enable per deployment,
/// e.g. <c>Serilog__MinimumLevel__Override__SimplArchive.Dav.Wire=Verbose</c>.</item>
/// <item><b>Unhandled-request Warning</b> — emitted <i>regardless</i> of the trace level
/// when a DAV request falls through unhandled (405/501), which usually means a native-client
/// compatibility gap (e.g. a method/path we don't serve). It points the operator at the
/// verbose trace for the details. Deduped per <c>method+status+segment</c> so client retries
/// don't flood the log.</item>
/// </list>
/// </summary>
public sealed class DavWireTraceMiddleware
{
    private const string Category = "SimplArchive.Dav.Wire";

    private readonly RequestDelegate _next;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _warnedUnhandled = new();
    private int _warned;

    public DavWireTraceMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
    {
        _next = next;
        _logger = loggerFactory.CreateLogger(Category);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsDav(context.Request))
        {
            await _next(context);
            return;
        }

        // Cheap path when the verbose trace is off: run the request, then still watch for
        // unhandled DAV requests (that Warning is independent of the trace level).
        if (!_logger.IsEnabled(LogLevel.Trace))
        {
            await _next(context);
            WarnIfUnhandled(context);
            return;
        }

        WarnOnceThatTracingIsActive();

        context.Request.EnableBuffering();
        var requestBody = await ReadRequestBodyAsync(context.Request);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context);
        }
        finally
        {
            buffer.Position = 0;
            var responseBody = await new StreamReader(buffer).ReadToEndAsync();
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);
            context.Response.Body = originalBody;

            _logger.LogTrace(
                "DAV {Method} {Path}{Query} depth={Depth} ua={UserAgent} -> {StatusCode}\n"
                + "request:\n{RequestBody}\nresponse:\n{ResponseBody}",
                context.Request.Method,
                context.Request.Path,
                context.Request.QueryString,
                context.Request.Headers.TryGetValue("Depth", out var depth) ? depth.ToString() : "0",
                UserAgent(context.Request),
                context.Response.StatusCode,
                requestBody,
                responseBody);

            WarnIfUnhandled(context);
        }
    }

    // A DAV request that fell through unhandled (405 Method Not Allowed / 501 Not
    // Implemented) usually means a native-client compatibility gap — the client used a
    // method/path we don't serve. Surface it at Warning naming the client, deduped so
    // retries don't flood. MKCOL/MKCALENDAR legitimately 405 on an existing collection.
    private void WarnIfUnhandled(HttpContext context)
    {
        var status = context.Response.StatusCode;
        var method = context.Request.Method;
        var unhandled = (status is StatusCodes.Status405MethodNotAllowed or StatusCodes.Status501NotImplemented)
            && method is not ("MKCOL" or "MKCALENDAR");
        if (!unhandled)
        {
            return;
        }

        var key = $"{method} {status} {FirstSegment(context.Request.Path)}";
        if (_warnedUnhandled.TryAdd(key, 0))
        {
            _logger.LogWarning(
                "Unhandled DAV request from client {UserAgent}: {Method} {Path} -> {StatusCode}. "
                + "Likely a native-client compatibility gap; set {Category}=Verbose to log the full "
                + "request/response.",
                UserAgent(context.Request), method, context.Request.Path, status, Category);
        }
    }

    private static string UserAgent(HttpRequest request) =>
        request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : "(unknown)";

    private static string FirstSegment(PathString path) =>
        path.Value?.Trim('/').Split('/', 2)[0] ?? "";

    // First time a verbose entry is actually written, raise one Warning: leaving this on
    // clutters the log and captures contact/calendar payloads — an admin should act (ported from SimplCalCon, ADR 0621).
    private void WarnOnceThatTracingIsActive()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _logger.LogWarning(
                "DAV wire tracing ({Category}) is enabled at Trace: request/response bodies — "
                + "including contact and calendar contents — are being logged. This is verbose and "
                + "unsafe for production; disable it when finished.", Category);
        }
    }

    // The DAV surface plus the RFC 6764 root-discovery methods (PROPFIND on "/").
    // ADAPTED: SimplArchive serves the two protocols under their own roots rather than one /dav, and the
    // WebDAV gateway has its own (/SimplArchive). The verb list keeps a DAV request visible wherever it lands,
    // which is exactly the case worth a warning — a client addressing a path we do not serve.
    private static bool IsDav(HttpRequest request) =>
        DavProtocol.ForPath(request.Path.Value ?? string.Empty) is not null
        || request.Path.StartsWithSegments("/.well-known")
        || request.Method is "PROPFIND" or "REPORT" or "PROPPATCH" or "MKCOL" or "MKCALENDAR";

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0)
        {
            return "";
        }

        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }
}
