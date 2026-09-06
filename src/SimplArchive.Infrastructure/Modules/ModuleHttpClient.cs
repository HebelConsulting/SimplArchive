using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The host's guarded <see cref="IModuleHttpClient"/> (ABI 0.6). Every module fetch runs through the
/// SSRF-guarded primary handler (ADR 0717 — DNS-rebinding-safe, redirect-refusing) AND is refused unless its
/// host is one the ACTING module declared in <see cref="IIndustryModule.OutboundHosts"/>. The acting module is
/// whoever <see cref="ModuleIdentityAccessor"/> names in this scope (the engine sets it before a handler runs),
/// so one registered client serves every module with each module's own allowlist — and a module can never
/// reach a host it did not declare, nor hold a raw <c>HttpClient</c> that would bypass the guard.
/// </summary>
public sealed class ModuleHttpClient : IModuleHttpClient
{
    private readonly HttpClient _http;
    private readonly ModuleIdentityAccessor _identity;
    private readonly IReadOnlyList<ModuleLoader.LoadedModule> _modules;

    public ModuleHttpClient(HttpClient http, ModuleIdentityAccessor identity, IReadOnlyList<ModuleLoader.LoadedModule> modules)
    {
        _http = http;
        _identity = identity;
        _modules = modules;
    }

    public Task<ModuleHttpResponse> GetAsync(string url, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, url, cancellationToken);

    public Task<ModuleHttpResponse> HeadAsync(string url, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Head, url, cancellationToken);

    private async Task<ModuleHttpResponse> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ModuleOutboundRefusedException(url, "not an absolute http/https URL");
        }

        // The declared-host gate — refused before any request leaves the process. The SSRF handler still
        // re-checks the resolved ADDRESS at connect time; this is the orthogonal "which hosts at all" question.
        var allowed = AllowedHosts();
        if (!allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ModuleOutboundRefusedException(url,
                $"host '{uri.Host}' is not in the module's declared OutboundHosts");
        }

        using var request = new HttpRequestMessage(method, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var body = method == HttpMethod.Head
            ? []
            : await response.Content.ReadAsByteArrayAsync(cancellationToken);

        return new ModuleHttpResponse(
            (int)response.StatusCode,
            response.IsSuccessStatusCode,
            response.Headers.ETag?.Tag,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentType?.ToString(),
            body);
    }

    private IReadOnlyList<string> AllowedHosts()
    {
        var moduleId = _identity.ModuleId
            ?? throw new InvalidOperationException(
                "A module HTTP request ran outside a module scope — there is no acting module to check its allowlist against.");
        var module = _modules.FirstOrDefault(m => m.Module.ModuleId == moduleId)?.Module
            ?? throw new InvalidOperationException($"Module '{moduleId}' is not loaded — its outbound allowlist is unknown.");
        return module.OutboundHosts;
    }
}

/// <summary>A module tried to reach a host it did not declare, or a malformed URL (ABI 0.6) — a programming
/// error in the module, refused before the request leaves the process.</summary>
public sealed class ModuleOutboundRefusedException(string url, string reason)
    : Exception($"The module's outbound request to '{url}' was refused: {reason}.")
{
    public string Url { get; } = url;

    public string Reason { get; } = reason;
}
