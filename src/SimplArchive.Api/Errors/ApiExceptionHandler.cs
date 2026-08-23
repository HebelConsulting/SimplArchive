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
        var (errorCode, statusCode, detail) = exception is ApiException apiException
            ? (apiException.ErrorCode, apiException.StatusCode, apiException.Message)
            : ("INTERNAL_ERROR", StatusCodes.Status500InternalServerError, "An unexpected error occurred.");

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
