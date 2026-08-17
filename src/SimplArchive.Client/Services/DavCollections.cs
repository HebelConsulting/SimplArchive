using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SimplArchive.Client.Services;

/// <summary>One addressbook or calendar, as the client tabs need it (#564).</summary>
/// <remarks>
/// The CalDAV/CardDAV home set answers the same question for EXTERNAL clients; ours speaks JSON and follows
/// rels, so it reads <c>/api/dav-collections</c> instead of parsing a multistatus to draw a tab.
/// </remarks>
public sealed class DavCollection
{
    public Guid Id { get; set; }

    /// <summary>Parent-qualified, so two same-named collections are tellable apart (ADR 0619).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary><c>addressbook</c> or <c>calendar</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? Color { get; set; }

    public bool Writable { get; set; }

    public bool IsPersonalDefault { get; set; }

    public List<LinkDto> Links { get; set; } = [];

    /// <summary>The advertised href for <paramref name="rel"/>, or null when the server did not offer it.</summary>
    /// <remarks>
    /// Null rather than a composed fallback: a rel the server did not advertise means the action is not
    /// available here (ADR 0543), and rebuilding the URL would paper over exactly what that rule prevents.
    /// </remarks>
    public string? Href(string rel) =>
        Links.FirstOrDefault(l => string.Equals(l.Rel, rel, StringComparison.Ordinal))?.Href;

    public sealed class LinkDto
    {
        public string Rel { get; set; } = string.Empty;

        public string Href { get; set; } = string.Empty;
    }
}

/// <summary>Reads the caller's addressbooks and calendars, following the <c>davCollections</c> me-rel.</summary>
public sealed class DavCollections
{
    private readonly HttpClient _http;
    private readonly ApiRoot _apiRoot;

    public DavCollections(HttpClient http, ApiRoot apiRoot)
    {
        _http = http;
        _apiRoot = apiRoot;
    }

    /// <param name="kind"><c>addressbook</c> or <c>calendar</c>.</param>
    /// <remarks>
    /// The kind rides as a QUERY on the advertised href, which is following it rather than composing one: the
    /// server owns the path, the caller owns the filter (ADR 0557). Appending a path SEGMENT would not be.
    /// </remarks>
    public async Task<List<DavCollection>> ListAsync(string kind, CancellationToken cancellationToken = default)
    {
        var href = await _apiRoot.MeHrefAsync("davCollections", cancellationToken);
        if (href is null)
        {
            // The rel is absent for a principal with no personal space — a ServiceAccount, say. An empty list
            // is the honest answer; the tab then shows its "nothing selected" line rather than an error.
            return [];
        }

        var response = await _http.GetFromJsonAsync<ListResponse>(
            $"{href}?kind={Uri.EscapeDataString(kind)}", cancellationToken);
        return response?.Collections ?? [];
    }

    private sealed class ListResponse
    {
        [JsonPropertyName("collections")]
        public List<DavCollection> Collections { get; set; } = [];
    }
}
