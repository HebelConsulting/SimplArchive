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

    public ApiCore(string accessToken)
    {
        AccessToken = accessToken;
        Http = new HttpClient { BaseAddress = new Uri(DesktopClientOptions.ApiBaseUrl) };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    /// <summary>This client's bearer token — also the RFC 8693 subject_token for impersonation.</summary>
    public string AccessToken { get; }

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
