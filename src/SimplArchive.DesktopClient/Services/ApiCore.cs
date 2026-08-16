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

    /// <summary>The row's advertised links, or null when it carries none.</summary>
    public static IReadOnlyDictionary<string, string>? ParseLinks(JsonElement item) =>
        SimplArchiveApiClient.ParseLinks(item);

    /// <summary>Maps a Problem-Details refusal to a localized <see cref="ApiActionException"/>.</summary>
    public static Task ThrowIfProblemAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken) =>
        SimplArchiveApiClient.ThrowIfProblemAsync(response, fallback, cancellationToken);
}
