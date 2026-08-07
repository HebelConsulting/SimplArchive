namespace SimplArchive.Api;

/// <summary>
/// Named rate-limiting policies (ADR 0546). The codebase had no rate limiting at all before external links; this
/// is deliberately narrow — one policy, for the one endpoint that is anonymous by design.
///
/// Extending throttling to the authenticated API is a separate decision with a different shape: those callers
/// have a principal to key on and legitimate bursty traffic, whereas this endpoint has neither.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>The anonymous external-link redemption endpoint.</summary>
    public const string ExternalLinks = "external-links";
}
