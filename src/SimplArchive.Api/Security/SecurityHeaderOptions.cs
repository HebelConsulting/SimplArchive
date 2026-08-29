namespace SimplArchive.Api.Security;

/// <summary>
/// The browser-hardening headers every response carries (ADR 0084, issue #844), bound from the
/// <c>SecurityHeaders</c> configuration section.
/// </summary>
/// <remarks>
/// Everything here is configurable for one reason: a deployment may terminate TLS and set these at its own edge
/// (an ingress, a reverse proxy), and two layers setting the same header is its own bug — a duplicated
/// <c>Content-Security-Policy</c> is intersected by the browser, so the stricter of two well-meant policies wins
/// and the app breaks in a way that looks like a code fault. So the app sets them by default and can be told
/// not to.
/// </remarks>
public sealed class SecurityHeaderOptions
{
    public const string SectionName = "SecurityHeaders";

    /// <summary>Master switch. On by default: the secure posture must be what you get for doing nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A complete policy to send verbatim instead of the composed one. For a deployment whose topology this
    /// code cannot know — a separate client origin, a CDN, an embedded viewer.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary>
    /// Extra origins to add to <c>connect-src</c> (and <c>img-src</c>), space-separated.
    /// </summary>
    /// <remarks>
    /// The object-storage public origin is added automatically from <c>ObjectStorage:PublicServiceUrl</c> —
    /// the browser uploads and previews STRAIGHT to storage over a presigned URL (the API never proxies bytes,
    /// ADR 0006/0213), so a policy that forgets it breaks every upload and every preview while the app looks
    /// otherwise healthy. Deriving it rather than asking for it again is the point: an operator cannot forget a
    /// value they never had to type.
    /// </remarks>
    public string? AdditionalConnectSources { get; set; }

    /// <summary>
    /// <c>Strict-Transport-Security</c>. Off by default, deliberately: HSTS is a promise a browser remembers for
    /// months, and making it on a deployment that is not actually on HTTPS — a LAN test, a sidecar behind a
    /// plain-HTTP proxy — locks users out of their own instance with no server-side undo.
    /// </summary>
    public bool EnableHsts { get; set; }

    public int HstsMaxAgeDays { get; set; } = 365;

    public bool HstsIncludeSubDomains { get; set; } = true;

    /// <summary>
    /// Origins allowed to call the API cross-origin. Empty (the default) registers no CORS policy at all, which
    /// is right for the shipped topology: the client is served from the same deployable as the API.
    /// </summary>
    public string[] CorsOrigins { get; set; } = [];
}
