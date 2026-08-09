using System.Net.Http.Json;

namespace SimplArchive.Client.Services;

/// <summary>
/// The API root discovery document, fetched once and cached — the client's single entry point into the API.
/// </summary>
/// <remarks>
/// ADR 0543: a client reaches an endpoint by following a rel the server advertised, and <b>the only URL it may
/// know is the API root</b>. Everything else composed from a string template is a route the server can no longer
/// rename — and, worse, a route the client keeps calling after it has moved. That is not hypothetical: the
/// external-links dialog composed <c>.../expiry</c>, kept calling it after the route became
/// <c>.../availability</c>, and reported the failure to the user as "could not generate the WebDAV password".
///
/// Cached because the root is a constant for a session: refetching it per call would turn one navigation into
/// two round trips, which is the usual reason teams abandon hypermedia. A miss returns null rather than throwing,
/// so a rel the server does not advertise disables the affordance (a missing rel means "not available to you,
/// here, now") instead of taking the page down.
///
/// Registered scoped (it holds the scoped HttpClient); in Blazor WASM that is one instance for the
/// app's lifetime, so the cache still spans the session.
/// </remarks>
public sealed class ApiRoot
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _rels;

    public ApiRoot(HttpClient http) => _http = http;

    /// <summary>
    /// The href for a root-level rel, or null when the server does not advertise it.
    /// </summary>
    public async Task<string?> HrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        var rels = await LoadAsync(cancellationToken);
        return rels.TryGetValue(rel, out var href) ? href : null;
    }

    /// <summary>
    /// The href for a rel the client cannot work without — throws rather than returning null.
    /// </summary>
    /// <remarks>
    /// For the collections a screen is *built around* (its tags, its users): if the server stopped advertising
    /// one, a null would surface as an empty list, which reads as "you have no tags" rather than "this is
    /// broken". Failing loudly is the honest outcome; use <see cref="HrefAsync"/> for optional affordances.
    /// </remarks>
    public async Task<string> RequireAsync(string rel, CancellationToken cancellationToken = default) =>
        await HrefAsync(rel, cancellationToken)
        ?? throw new InvalidOperationException($"The API root does not advertise the '{rel}' rel.");

    /// <summary>
    /// The href for a rel on the caller's own "me" resource (issue #416) — their password, photo, MFA, passkeys,
    /// WebDAV password and personal repository all hang off it rather than off the root.
    /// </summary>
    /// <remarks>
    /// Cached like the root: it is a constant for the session, and refetching per call would make following a rel
    /// cost two round trips — the usual reason a codebase gives up on hypermedia and goes back to string paths.
    /// Null when the rel is absent, which is what a service-account principal sees (it has no personal account),
    /// so a caller disables the affordance rather than failing.
    /// </remarks>
    public async Task<string?> MeHrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        if (_meRels is null)
        {
            // Resolve the root href BEFORE taking the gate. SemaphoreSlim is not reentrant, so calling
            // HrefAsync (which takes the same gate) while holding it deadlocks — and because the gate is then
            // never released, every later ApiRoot call in the app blocks behind it too. That is not a subtle
            // failure: the whole workbench freezes, which is how it took out all four CI legs at once.
            var meHref = await HrefAsync("me", cancellationToken);

            // Its own gate, so a me-lookup and a root-lookup can never wait on each other again.
            await _meGate.WaitAsync(cancellationToken);
            try
            {
                if (_meRels is null)
                {
                    var me = meHref is null ? null : await _http.GetFromJsonAsync<RootResponse>(meHref, cancellationToken);
                    _meRels = me?.Links
                        .Where(l => !string.IsNullOrEmpty(l.Rel) && !string.IsNullOrEmpty(l.Href))
                        .ToDictionary(l => l.Rel, l => Relative(l.Href)) ?? [];
                }
            }
            finally
            {
                _meGate.Release();
            }
        }

        return _meRels.TryGetValue(rel, out var href) ? href : null;
    }

    /// <summary>As <see cref="MeHrefAsync"/>, for a rel the caller cannot work without.</summary>
    public async Task<string> RequireMeAsync(string rel, CancellationToken cancellationToken = default) =>
        await MeHrefAsync(rel, cancellationToken)
        ?? throw new InvalidOperationException($"The 'me' resource does not advertise the '{rel}' rel.");

    private readonly SemaphoreSlim _meGate = new(1, 1);
    private Dictionary<string, string>? _meRels;

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_rels is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate: several components render at once on first load, so without this the
            // root would be fetched once per concurrent caller.
            if (_rels is { } inner)
            {
                return inner;
            }

            var root = await _http.GetFromJsonAsync<RootResponse>("api", cancellationToken);
            _rels = root?.Links
                .Where(l => !string.IsNullOrEmpty(l.Rel) && !string.IsNullOrEmpty(l.Href))
                .ToDictionary(l => l.Rel, l => Relative(l.Href))
                ?? [];
            return _rels;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The server advertises absolute-from-root hrefs ("/api/tags"); HttpClient here has a BaseAddress, so a
    // leading slash would escape it. Same normalisation the components already do for per-resource rels.
    private static string Relative(string href) => href.StartsWith('/') ? href[1..] : href;

    private sealed record RootResponse
    {
        public List<LinkResponse> Links { get; set; } = [];
    }

    private sealed record LinkResponse
    {
        public string Rel { get; set; } = "";

        public string Href { get; set; } = "";
    }
}
