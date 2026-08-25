using System.Diagnostics;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Transform;
using Microsoft.Extensions.Logging;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// Traces the object-storage exchange at the wire, so "what exactly passed between us and the store?" is
/// answerable by turning Trace on (ADR 0626).
/// </summary>
/// <remarks>
/// <para>
/// This sits in the AWS SDK's own pipeline rather than wrapping our client's methods, because the two answer
/// different questions. Our methods know what we MEANT to ask; only the pipeline knows what was actually sent —
/// which host the request went to, which path style it used, what the store answered. Every object-storage
/// surprise this project has had was of the second kind: <c>ForcePathStyle</c> and the presigned-URL scheme
/// default were both cases where the intent was right and the wire was not.
/// </para>
/// <para>
/// <b>Redaction is a whitelist, on purpose.</b> A blacklist ("log everything except Authorization") leaks the
/// day the SDK adds a header, and the leak is silent. So only headers named in <see cref="SafeHeaders"/> are
/// emitted; anything else is counted, never printed. Bodies are never logged at all — an object's bytes are the
/// user's document, and their first bytes are the most revealing part of them.
/// </para>
/// </remarks>
public sealed class S3WireTraceHandler : PipelineHandler
{
    /// <summary>
    /// Headers safe to print. Everything absent from this set is a header we have not thought about, which is
    /// exactly the set that must not reach a log.
    /// </summary>
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type",
        "content-length",
        "content-range",
        "accept-ranges",
        "etag",
        "last-modified",
        "date",
        "server",
        "x-amz-request-id",
        "x-amz-id-2",
        "x-amz-version-id",
        "x-amz-object-lock-mode",
        "x-amz-object-lock-retain-until-date",
        "x-amz-object-lock-legal-hold",
    };

    // A FUNC, not an ILogger, and the reason is an ordering trap identical to #753's.
    //
    // This handler is installed from CustomizeRuntimePipeline, which the BASE AmazonS3Client constructor calls —
    // before the derived constructor body has assigned its logger field. Captured by value, the handler would
    // hold null forever and throw a NullReferenceException on the first traced call, taking down the storage
    // operation it exists only to describe. Captured by reference, the field is read at INVOKE time, long after
    // construction has finished. (Found by running it against a real store, not by a test.)
    private readonly Func<ILogger?> _log;

    public S3WireTraceHandler(Func<ILogger?> log) => _log = log;

    public override void InvokeSync(IExecutionContext executionContext)
    {
        // Not every caller is async, and a seam that is answerable only on one of its two paths is not
        // answerable — the sync path is the one a background worker is most likely to take.
        var clock = BeginExchange(executionContext);
        try
        {
            base.InvokeSync(executionContext);
        }
        catch (Exception e)
        {
            LogFailure(executionContext, clock, e);
            throw;
        }

        LogCompletion(executionContext, clock);
    }

    public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        var clock = BeginExchange(executionContext);
        T response;
        try
        {
            response = await base.InvokeAsync<T>(executionContext);
        }
        catch (Exception e)
        {
            LogFailure(executionContext, clock, e);
            throw;
        }

        LogCompletion(executionContext, clock);
        return response;
    }

    private Stopwatch? BeginExchange(IExecutionContext executionContext)
    {
        // The whole handler costs nothing when Trace is off, which is every environment by default (ADR 0430) —
        // so the guard is what lets this sit on the hot path of every byte the system stores.
        var logger = _log();
        if (logger is null || !logger.IsEnabled(LogLevel.Trace))
        {
            return null;
        }

        // Defensive, and NOT theoretical: the pipeline calls this for operations whose request is not populated
        // at this stage, and an exception thrown from a logging handler would take down the storage call it was
        // only supposed to describe. A trace that can break the thing it observes is worse than no trace — so
        // every field below is treated as absent-able, and the clock still starts so the response half matches.
        var request = executionContext.RequestContext?.Request;
        if (request is null)
        {
            logger.LogTrace(
                "Object storage → {Operation} (no marshalled request to describe at this stage)",
                executionContext.RequestContext?.RequestName ?? "(unknown)");
            return Stopwatch.StartNew();
        }

        logger.LogTrace(
            "Object storage → {HttpMethod} {Endpoint}{ResourcePath} (operation {Operation}, headers {RequestHeaders})",
            request.HttpMethod ?? "(none)",
            request.Endpoint?.ToString() ?? "(unresolved)",
            ResolvePath(request),
            executionContext.RequestContext?.RequestName ?? "(unknown)",
            Describe(request.Headers));

        return Stopwatch.StartNew();
    }

    private void LogCompletion(IExecutionContext executionContext, Stopwatch? clock)
    {
        if (clock is null)
        {
            return;
        }

        var logger = _log();
        if (logger is null)
        {
            return;
        }

        var response = executionContext.ResponseContext?.HttpResponse;
        logger.LogTrace(
            "Object storage ← {StatusCode} for {Operation} in {ElapsedMs} ms (headers {ResponseHeaders})",
            response is null ? "(no response)" : ((int)response.StatusCode).ToString(),
            executionContext.RequestContext?.RequestName ?? "(unknown)",
            clock.ElapsedMilliseconds,
            response is null ? "(none)" : Describe(response));
    }

    private void LogFailure(IExecutionContext executionContext, Stopwatch? clock, Exception e)
    {
        if (clock is null)
        {
            return;
        }

        // Trace rather than Error: the caller decides whether this is a failure. A "does this object have a
        // lock?" GET answering 404 is an expected answer here and a swallowed one upstream — logging it as an
        // error would make every unlocked object look like an incident.
        var logger = _log();
        if (logger is null)
        {
            return;
        }

        logger.LogTrace(
            "Object storage ✕ {Operation} threw {ExceptionType} after {ElapsedMs} ms: {ExceptionMessage}",
            executionContext.RequestContext?.RequestName ?? "(unknown)",
            e.GetType().Name,
            clock.ElapsedMilliseconds,
            e.Message);
    }

    // The marshalled ResourcePath is a TEMPLATE — "/{Key+}" — with the values held separately in PathResources.
    // Logging it unresolved was measured against a real store and named no object at all, which for the seam
    // that stores every document is most of the point of the line.
    private static string ResolvePath(IRequest request)
        => ResolvePath(request.ResourcePath, request.PathResources);

    /// <summary>Substitutes a marshalled path's placeholders. Public so its failure mode stays testable.</summary>
    public static string ResolvePath(string? resourcePath, IDictionary<string, string>? pathResources)
    {
        var path = resourcePath ?? string.Empty;
        if (pathResources is not { Count: > 0 })
        {
            return path;
        }

        foreach (var (token, value) in pathResources)
        {
            // The dictionary's keys already CARRY their braces ("{Key+}"), so wrapping them again matches
            // nothing and silently leaves the template in place — which is how the first attempt at this looked
            // correct and logged "/{Key+}" for every object written.
            var placeholder = token.StartsWith('{') ? token : $"{{{token}}}";
            path = path.Replace(placeholder, value, StringComparison.Ordinal);
        }

        return path;
    }

    private static string Describe(IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return "(none)";
        }

        var safe = headers.Where(h => SafeHeaders.Contains(h.Key)).Select(h => $"{h.Key}={h.Value}").ToList();
        return Format(safe, headers.Count - safe.Count);
    }

    private static string Describe(IWebResponseData response)
    {
        var names = response.GetHeaderNames() ?? [];
        var safe = names
            .Where(SafeHeaders.Contains)
            .Select(name => $"{name}={response.GetHeaderValue(name)}")
            .ToList();
        return Format(safe, names.Length - safe.Count);
    }

    // The withheld COUNT is deliberate. A reader who can see that four headers were not printed knows the
    // whitelist is why they cannot see them; a silently shortened list reads as "that is all there was".
    private static string Format(List<string> safe, int withheld)
    {
        var shown = safe.Count == 0 ? "(none safe to print)" : string.Join(", ", safe);
        return withheld <= 0 ? shown : $"{shown} [+{withheld} withheld]";
    }
}
