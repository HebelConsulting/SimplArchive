using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SimplArchive.Api.HealthChecks;

/// <summary>
/// Minimal JSON instead of ASP.NET Core's built-in plain-text "Healthy"/"Unhealthy" default — see ADR
/// "Health check endpoints". A probe only ever looks at the HTTP status code, but this gives a human or
/// dashboard something readable on a direct curl.
/// </summary>
public static class HealthCheckResponseWriter
{
    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
