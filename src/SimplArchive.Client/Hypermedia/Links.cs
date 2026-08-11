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
}
