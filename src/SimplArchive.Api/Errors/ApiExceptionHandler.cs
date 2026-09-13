using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace SimplArchive.Api.Errors;

// Registered via app.UseExceptionHandler() — see ADR "Hypermedia envelope and Problem Details errors
// (foundation slice)". Translates an ApiException into its own errorCode/status; any other unhandled
// exception falls back to a generic 500 with errorCode "INTERNAL_ERROR" rather than leaking exception
// details to the client. Logs by severity (ADR "Enterprise-grade structured logging with Serilog"): a 500 is
// an Error (an exception for an admin to investigate), a handled 4xx business/validation error is Debug (normal
// control flow — no clutter at Information).
public class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _logger;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // THE CALLER LEFT. Nothing failed here, and saying otherwise costs twice over: it logs an Error an
        // administrator is asked to investigate, and it writes a body to a socket nobody is reading.
        //
        // A cancelled request unwinds through whatever await happened to be pending, so the exception names
        // an innocent bystander — on the kiosk (#1142) it surfaced from an S3 PUT and read as object storage
        // timing out. It was not: the AWS SDK's default timeout is ALSO 100 s, which is exactly the default
        // HttpClient.Timeout the Blazor and desktop clients both inherit, and the two coincide into a very
        // convincing wrong answer. The log's own ordering is what settled it — the PUT was logged in the same
        // second it threw, so it had not been running 100 s; it met a token that was already cancelled.
        //
        // 499 is nginx's, not an IANA code, and is chosen precisely because it never reaches a client: it
        // marks the access log so "the user gave up" stays distinguishable from "we broke", which is the
        // distinction that was lost. Returning 500 here is what made a slow operation look like a fault.
        if (httpContext.RequestAborted.IsCancellationRequested && exception is OperationCanceledException)
        {
            _logger.LogDebug(
                "Request {Method} {Path} was abandoned by the caller before it finished; no response was written.",
                httpContext.Request.Method, httpContext.Request.Path);

            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = 499;
            }

            return true;
        }

        string? localizedByModule = null;
        var (errorCode, statusCode, detail) = exception switch
        {
            ApiException apiException => (apiException.ErrorCode, apiException.StatusCode, apiException.Message),
            // A module's refusal (ADR 0737): same wire shape as a core one — a module error must be
            // indistinguishable from a native error, which is the whole point of real controllers.
            SimplArchive.ModuleAbi.ModuleApiException moduleException =>
                (moduleException.ErrorCode, moduleException.StatusCode, moduleException.Message),
            _ => ("INTERNAL_ERROR", StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        // The module's catalog text for the request culture (ABI 0.10, ADR 0767): the detail becomes the
        // localized sentence and the problem carries the module id — the clients' license to render it
        // (their no-server-detail rule guards against unlocalizable English, which this is not). The acting
        // module comes from the request's module scope when set; otherwise the code finds its module, since
        // module codes are prefixed by convention. Falls back to the composed invariant message untouched.
        if (exception is SimplArchive.ModuleAbi.ModuleApiException m)
        {
            var modules = httpContext.RequestServices.GetService<IReadOnlyList<Infrastructure.Modules.ModuleLoader.LoadedModule>>() ?? [];
            var actingModule = httpContext.RequestServices.GetService<Infrastructure.Modules.ModuleIdentityAccessor>()?.ModuleId;
            // The REQUEST's culture, from the feature — never CurrentUICulture: the exception unwound out
            // of the localization middleware's async scope before the handler ran, so the ambient culture
            // has reverted to the default and every handler-thrown refusal resolved to English while the
            // module marker claimed it was localized (found live 2026-09-08; the engine-composed
            // refusals never hit this because they localize in-request).
            var culture = httpContext.Features.Get<Microsoft.AspNetCore.Localization.IRequestCultureFeature>()
                ?.RequestCulture.UICulture ?? System.Globalization.CultureInfo.CurrentUICulture;
            if (Infrastructure.Modules.ModuleTextResolver.Resolve(
                    modules, actingModule, m.ErrorCode, m.Args.Count > 0 ? m.Args[0] : null,
                    culture) is { } resolved)
            {
                detail = Infrastructure.Modules.ModuleTextResolver.Format(resolved.Template, m.Args);
                localizedByModule = resolved.ModuleId;
            }
        }

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Request {Method} {Path} failed with {ErrorCode} ({StatusCode})",
                httpContext.Request.Method, httpContext.Request.Path, errorCode, statusCode);
        }
        else
        {
            _logger.LogDebug("Request {Method} {Path} rejected with {ErrorCode} ({StatusCode}): {Detail}",
                httpContext.Request.Method, httpContext.Request.Path, errorCode, statusCode, detail);
        }

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode),
            Detail = detail,
            Instance = httpContext.Request.Path,
        };
        problemDetails.Extensions["errorCode"] = errorCode;
        if (localizedByModule is not null)
        {
            problemDetails.Extensions["module"] = localizedByModule;
        }
        foreach (var (key, value) in (exception as ApiException)?.Extensions ?? new Dictionary<string, object?>())
        {
            problemDetails.Extensions[key] = value;
        }

        httpContext.Response.StatusCode = statusCode;
        // RFC 7807's own media type, not plain application/json — see ADR "API versioning and error
        // response model".
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, contentType: "application/problem+json", cancellationToken);

        return true;
    }
}
