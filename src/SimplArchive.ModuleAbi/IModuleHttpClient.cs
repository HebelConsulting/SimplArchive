namespace SimplArchive.ModuleAbi;

/// <summary>
/// A GUARDED outbound HTTP client the host injects into a module's registered services (ABI 0.6). Every
/// request runs through the core's SSRF policy (ADR 0717) and is refused unless its host is one the module
/// DECLARED in <see cref="IIndustryModule.OutboundHosts"/> — so a module's network egress is as enumerable,
/// and as contained, as its archive reach through <see cref="IModuleArchiveFacade"/>. A module never holds a
/// raw <c>HttpClient</c> (which would bypass the guard): it writes fetch-and-parse logic against this narrow
/// surface, and the host owns transport, the allowlist, and the metadata-endpoint refusals.
/// </summary>
public interface IModuleHttpClient
{
    /// <summary>GETs a declared-host URL and returns the response for the module to read (bytes, JSON, text).
    /// A host outside <see cref="IIndustryModule.OutboundHosts"/>, or a blocked address, is refused before
    /// any request leaves the process.</summary>
    Task<ModuleHttpResponse> GetAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>HEADs a declared-host URL — the response headers without the body, for a cheap "did it
    /// change?" probe (the DABS <c>ETag</c>/<c>Content-Length</c> check before re-downloading a chart that
    /// has not moved). Same allowlist gate as <see cref="GetAsync(string,System.Threading.CancellationToken)"/>.</summary>
    Task<ModuleHttpResponse> HeadAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>GETs a declared-host URL with request headers — the authenticated read (ABI 0.9, ADR 0766):
    /// a NOTAM query carries its <c>Authorization: Bearer</c> this way. Only a small safe set of header
    /// names is accepted (<c>Authorization</c>, <c>Accept</c>, <c>Accept-Language</c>, <c>If-None-Match</c>);
    /// anything else — above all <c>Host</c> — is refused before the request leaves, because a module's
    /// egress must stay as enumerable as its allowlist. Same host gate as <see cref="GetAsync(string,System.Threading.CancellationToken)"/>.</summary>
    Task<ModuleHttpResponse> GetAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);

    /// <summary>POSTs a form (<c>application/x-www-form-urlencoded</c>) to a declared-host URL — what an
    /// OAuth token exchange is (ABI 0.9, ADR 0766). Same host gate and header whitelist as the
    /// authenticated GET; the form values are the module's business and are never logged by the host.</summary>
    Task<ModuleHttpResponse> PostFormAsync(string url, IReadOnlyDictionary<string, string> form,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A module-visible HTTP response (ABI 0.6): the status, the headers a fetch decision needs, and the body
/// bytes (empty for a HEAD). Deliberately a small DTO rather than the framework's <c>HttpResponseMessage</c>
/// — a module reads what it asked for, and the ABI stays free of transport types it should not depend on.
/// </summary>
/// <param name="StatusCode">The HTTP status code.</param>
/// <param name="IsSuccess">Whether the status is 2xx.</param>
/// <param name="ETag">The response <c>ETag</c> if present — the module's preferred change token.</param>
/// <param name="ContentLength">The response <c>Content-Length</c> if present — the fallback change token.</param>
/// <param name="ContentType">The response <c>Content-Type</c> if present.</param>
/// <param name="Content">The body bytes; empty for a HEAD or a body-less response.</param>
public sealed record ModuleHttpResponse(
    int StatusCode,
    bool IsSuccess,
    string? ETag,
    long? ContentLength,
    string? ContentType,
    byte[] Content);
