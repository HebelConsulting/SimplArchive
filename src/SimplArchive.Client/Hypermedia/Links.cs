namespace SimplArchive.Client.Hypermedia;

/// <summary>
/// Reading an advertised address out of a resource's links (ADR 0543). A client follows these; it never
/// composes the address itself.
/// </summary>
public static class Links
{
    /// <summary>
    /// The address advertised for <paramref name="rel"/>, or null when the resource did not offer it — which
    /// is meaningful: it means "not available to you, here, now", so the caller hides the affordance rather
    /// than trying and handling a refusal (ADR 0543).
    /// </summary>
    /// <remarks>
    /// Absolute addresses (a presigned storage URL) are returned untouched; server-relative ones lose their
    /// leading slash so they compose correctly against the HttpClient's BaseAddress.
    /// </remarks>
    public static string? Href(List<LinkResponse>? links, string rel)
    {
        var href = links?.FirstOrDefault(l => l.Rel == rel)?.Href;
        if (href is null)
        {
            return null;
        }

        return href.StartsWith("http://", StringComparison.Ordinal) || href.StartsWith("https://", StringComparison.Ordinal)
            ? href
            : href.TrimStart('/');
    }

    /// <summary>
    /// A rel → href map for a resource's advertised links, so a caller can carry a row's ADDRESSES rather than
    /// its id alone (ADR 0555). Returns <c>null</c> when the resource advertised nothing, which is meaningful:
    /// it means no action is available here, not that the map is empty by accident.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? RelMap(List<LinkResponse>? links)
    {
        if (links is null || links.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            if (!string.IsNullOrEmpty(l.Rel) && Href(links, l.Rel) is { } href)
            {
                map[l.Rel] = href;
            }
        }

        return map.Count == 0 ? null : map;
    }

    /// <summary>
    /// The address advertised for <paramref name="rel"/> in an already-read rel map, for a caller that cannot
    /// proceed without it. Throws rather than composing a fallback: a rel that was not advertised means the
    /// action is not available here (ADR 0543), and rebuilding the path would paper over exactly what this
    /// replaces.
    /// </summary>
    public static string Required(IReadOnlyDictionary<string, string>? links, string rel) =>
        links is not null && links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The '{rel}' rel was not advertised (ADR 0543).");

    /// <summary>
    /// Sends AT a rel: the address and the METHOD both come from the link the server advertised, so the two
    /// cannot disagree.
    /// </summary>
    /// <remarks>
    /// ADR 0543 says rel names are the compatibility surface, and #1173 proved the address half — fourteen
    /// routes moved and no client learned a new one. The method half was never true: a <see cref="Link"/>
    /// carries a <c>Method</c> and every call site named the verb itself, so a route that kept its rel and
    /// changed its verb still broke every client. Nine had to be corrected by hand.
    ///
    /// The point of this helper is that a caller CANNOT name a verb, which is the same move ADR 0795 made on
    /// the server: turn a silent omission into something impossible to write rather than merely discouraged.
    ///
    /// Throws when the rel was not advertised — a missing rel means "not available to you, here, now", so a
    /// caller that reached here without checking has a bug, and composing a fallback would hide it.
    /// </remarks>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, List<LinkResponse>? links, string rel, object? body = null, CancellationToken cancellationToken = default)
    {
        if (links?.FirstOrDefault(l => l.Rel == rel) is not { } link || Href(links, rel) is not { } href)
        {
            throw new InvalidOperationException($"The '{rel}' rel was not advertised (ADR 0543).");
        }

        using var request = new HttpRequestMessage(Method(link, rel), href);
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        return await http.SendAsync(request, cancellationToken);
    }

    // An advertised method the client does not recognise is a refusal, not a default: guessing POST is how a
    // client comes to write where the server meant it to read.
    private static HttpMethod Method(LinkResponse link, string rel) => link.Method.ToUpperInvariant() switch
    {
        "GET" => HttpMethod.Get,
        "PUT" => HttpMethod.Put,
        "POST" => HttpMethod.Post,
        "DELETE" => HttpMethod.Delete,
        "HEAD" => HttpMethod.Head,
        _ => throw new InvalidOperationException($"The '{rel}' rel advertised no usable method ('{link.Method}')."),
    };
}
