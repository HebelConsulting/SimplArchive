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
    // A healthy check has no description, and emitting "description": null for each of them would make the
    // common response noisier than the shape ADR 0205 deliberately kept minimal.
    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        // The description rides along when a check set one, because the status alone cannot distinguish the two
        // things a reader most needs to tell apart: a database that refused our credential from one that is
        // simply full (#1287, ADR 0807). A probe still reads only the status code; this is for the human.
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, OmitNulls));
    }
}
