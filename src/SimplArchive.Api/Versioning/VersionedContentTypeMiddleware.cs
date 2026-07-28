using Asp.Versioning;

namespace SimplArchive.Api.Versioning;

// Rewrites the response Content-Type from a plain application/json or application/xml (whichever the
// stock formatter actually wrote — e.g. when no Accept header, or a generic "application/json"/"*/*",
// was sent) to the negotiated application/vnd.simplarchive.v{version}+{format} — see ADR "Media-type/
// Accept-header API versioning (foundation slice)", ADR "JSON/XML content negotiation". When a client
// explicitly requests the vendor+version+format media type, ASP.NET Core's own content negotiation
// already echoes that exact value back as Response.ContentType, so this is a no-op in that case. A
// lightweight rewrite rather than a full custom OutputFormatter per version, since only one version
// exists today and every version currently serializes identically.
public class VersionedContentTypeMiddleware
{
    // Parsed via ApiVersionParser.Default (matching ADR 0012's "v1" example, major-only) rather than
    // constructed as new ApiVersion(1, 0) — the two produce different ToString() formats ("1" vs "1.0"),
    // which would make an unspecified-version response inconsistent with an explicit "v1" request.
    private static readonly ApiVersion FallbackVersion = ApiVersionParser.Default.TryParse("1", out var version) ? version! : new ApiVersion(1, 0);

    private readonly RequestDelegate _next;

    public VersionedContentTypeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The vendor media-type versioning applies only to the versioned API surface (/api — see ADR "API
        // routes under an /api prefix"). Protocol/infrastructure endpoints (/connect/*, /.well-known/*,
        // /health/*) must return their standard content types: the OIDC token/userinfo responses in
        // particular must stay application/json, or the Blazor auth library (oidc-client) rejects them
        // ("There was an error signing in.").
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        context.Response.OnStarting(() =>
        {
            var contentType = context.Response.ContentType;

            var format = contentType switch
            {
                not null when contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) => "json",
                not null when contentType.StartsWith("application/xml", StringComparison.OrdinalIgnoreCase) => "xml",
                not null when contentType.StartsWith("text/xml", StringComparison.OrdinalIgnoreCase) => "xml",
                _ => null,
            };

            if (format is not null)
            {
                var version = context.Features.Get<IApiVersioningFeature>()?.RequestedApiVersion ?? FallbackVersion;
                var charsetSuffix = contentType!.Contains(';') ? contentType[contentType.IndexOf(';')..] : string.Empty;
                context.Response.ContentType = $"application/vnd.simplarchive.v{version}+{format}{charsetSuffix}";
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
