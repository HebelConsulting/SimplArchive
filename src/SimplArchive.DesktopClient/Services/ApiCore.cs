using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The one authenticated HTTP core every per-area client shares (#443, tranche 1): the bearer-carrying
/// <see cref="Http"/>, the API-root link cache (<see cref="RootHrefAsync"/> — the root is the ONE URL a
/// client may know, ADR 0543; its rel set is structurally fixed, so caching it is allowed, ADR 0557), and
/// the shared wire helpers (<see cref="ParseLinks"/>, <see cref="ThrowIfProblemAsync"/>).
/// </summary>
/// <remarks>
/// Extracted from <c>SimplArchiveApiClient</c>, which now wraps this core and forwards to it — each area
/// tranche moves its methods onto a per-area client taking this core, and the god client shrinks
/// monotonically (the ceiling guard banks each step). Error translation stays the Problem-Details error-code
/// mapping: the code is the stable, language-neutral contract (issue #424).
/// </remarks>
public sealed class ApiCore
{
    /// <summary>A token-free client for presigned-URL transfers — a presigned PUT/GET carries its own auth.</summary>
    public static readonly HttpClient Anonymous = new();

    private IReadOnlyDictionary<string, string>? _rootLinks;
    private readonly SemaphoreSlim _rootGate = new(1, 1);

    private readonly string _apiRootUrl;
    private readonly TokenSession.Holder _session;

    public ApiCore(string accessToken)
    {
        _apiRootUrl = DesktopClientOptions.ApiBaseUrl;

        // Honour the token this was CONSTRUCTED with. The login path records the full session first (with its
        // refresh token) and this leaves it alone; every other caller — impersonation, and every test that
        // builds a client from a bare token — gets a session seeded from what it passed.
        //
        // Without this the handler finds no session, sends no Authorization header, and every request 401s.
        // That is not hypothetical: it took 115 desktop tests down in one run, all of them reporting the same
        // 401 as if the server had rejected a credential rather than never being offered one.
        //
        // MaxValue, not "already expired": we do not know this token's lifetime and cannot renew it without a
        // refresh token, so claiming it needs renewal would make every request attempt a refresh it cannot do.
        // Adopt the recorded session when this client was built from the SAME token the login recorded — that
        // is the one case where a refresh token belongs to this client. Otherwise the client owns a private
        // session seeded from the token it was given, and does not renew.
        //
        // Deliberately NOT the shared store as the live source: it is keyed by SERVER, which is right for
        // persistence and wrong for identity — two clients for different users against one server would share
        // a slot and the second would silently become the first.
        var recorded = TokenSessions.Current.For(_apiRootUrl);
        var session = recorded is { RefreshToken.Length: > 0 }
            && string.Equals(recorded.AccessToken, accessToken, StringComparison.Ordinal)
                ? recorded
                // MaxValue, not "already expired": the lifetime is unknown and there is no refresh token, so
                // claiming it needs renewal would make every request attempt one it cannot perform.
                : new TokenSession(accessToken, null, DateTimeOffset.MaxValue);

        _session = new TokenSession.Holder(session);

        Http = new HttpClient(new RenewingAuthHandler(_apiRootUrl, _session, new HttpClientHandler()))
        {
            BaseAddress = new Uri(_apiRootUrl),
        };
    }

    /// <summary>
    /// This client's bearer token — also the RFC 8693 subject_token for impersonation.
    /// </summary>
    /// <remarks>
    /// Read from the live session rather than captured at construction, so a caller that needs the raw token
    /// (impersonation) gets the one currently valid rather than the one this object was born with.
    /// </remarks>
    public string AccessToken => _session.Value?.AccessToken ?? string.Empty;

    /// <summary>The authenticated HttpClient every area client sends through.</summary>
    public HttpClient Http { get; }

    /// <summary>The API root's advertised href for <paramref name="rel"/> (cached after the first read).</summary>
    public async Task<string> RootHrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        if (_rootLinks is null)
        {
            await _rootGate.WaitAsync(cancellationToken);
            try
            {
                _rootLinks ??= await GetRootLinksAsync(cancellationToken);
            }
            finally
            {
                _rootGate.Release();
            }
        }

        return _rootLinks.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The API root does not advertise the '{rel}' rel.");
    }

    /// <summary>The API root's link relations, uncached. Note "api" carries no slash — not a composed path.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetRootLinksAsync(CancellationToken cancellationToken = default)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        using var response = await Http.GetAsync("api", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return links;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("links", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in items.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } name
                    && link.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } value)
                {
                    links[name] = value;
                }
            }
        }

        return links;
    }

    /// <summary>
    /// Loads every page of a cursor-paginated listing (ADR 0207): follows the envelope's `next` rel until
    /// exhausted, parsing <paramref name="arrayProperty"/>'s items with <paramref name="parse"/>.
    /// </summary>
    public async Task<List<T>> LoadPagedAsync<T>(string url, string arrayProperty, Func<JsonElement, T> parse, CancellationToken cancellationToken,
        Action<JsonElement>? onPage = null)
    {
        var items = new List<T>();
        string? next = url;

        while (next is not null)
        {
            var page = await Http.GetFromJsonAsync<JsonElement>(next, cancellationToken);
            onPage?.Invoke(page);
            if (page.TryGetProperty(arrayProperty, out var array))
            {
                items.AddRange(array.EnumerateArray().Select(parse));
            }

            next = FindLink(page, "next");
        }

        return items;
    }

    /// <summary>The resource's advertised href for <paramref name="rel"/>, or null.</summary>
    public static string? FindLink(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links))
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.GetProperty("rel").GetString() == rel)
            {
                return link.GetProperty("href").GetString();
            }
        }

        return null;
    }

    /// <summary>The row's advertised links, or null when it carries none.</summary>
    public static IReadOnlyDictionary<string, string>? ParseLinks(JsonElement item) =>
        SimplArchiveApiClient.ParseLinks(item);

    /// <summary>Maps a Problem-Details refusal to a localized <see cref="ApiActionException"/>.</summary>
    public static Task ThrowIfProblemAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken) =>
        SimplArchiveApiClient.ThrowIfProblemAsync(response, fallback, cancellationToken);
    public async Task<byte[]?> GetPhotoAsync(string photoHref, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(photoHref, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task DeletePhotoAsync(Task<string> photoHref, CancellationToken cancellationToken)
    {
        using var response = await Http.DeleteAsync(await photoHref, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>PUTs a PNG to an advertised photo href, translating the refusals a caller can act on.</summary>
    public async Task PutPhotoAsync(string url, byte[] png, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var response = await Http.PutAsync(url, content, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to change this photo.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("That image could not be used as a profile photo.");
        }

        response.EnsureSuccessStatusCode();
    }



    // The href a resource advertises for a rel, or null when it doesn't offer one. A missing rel is meaningful —
    // it means "not available here" — so callers branch on null rather than composing a URL (ADR 0543).
    // internal: IntrayApi follows rels too, since the intray calls moved there (#443 direction).
    public static string? RelHref(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.TryGetProperty("rel", out var r) && r.GetString() == rel
                && link.TryGetProperty("href", out var h) && h.GetString() is { Length: > 0 } href)
            {
                return href.TrimStart('/');
            }
        }

        return null;
    }

    // Follows a rel off a resource the client just READ or just CREATED — the case where the address is already
    // in hand and only needs picking up, as opposed to DocumentRelAsync's "I hold an id, fetch the resource".
    public static string RequireRel(JsonElement resource, string rel, string what) =>
        ApiCore.ParseLinks(resource) is { } links && links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"{what} advertised no '{rel}' rel (ADR 0543).");

    public static string RequireHref(IAdvertisesLinks row, string rel) =>
        row.Href(rel)
        ?? throw new InvalidOperationException($"The row '{row.Name}' advertised no '{rel}' rel (ADR 0543/0555).");


}
