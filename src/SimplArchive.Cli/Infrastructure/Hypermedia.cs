using System.Text.Json;

namespace SimplArchive.Cli.Infrastructure;

/// <summary>
/// Follows link relations, so this tool knows exactly one URL (ADR 0543).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule is not the clients' alone.</b> A CLI that composes <c>api/…</c> paths is a second client with
/// its own private copy of the URL space, and it breaks in the same way: rename a route and the application
/// keeps working while the tool starts answering 404. Rel names are the compatibility surface; paths are not.
/// </para>
/// <para>
/// <b>The API root is the one address allowed</b>, and it is spelled <c>api</c> with no slash — a relative
/// path resolved against the installation, not a composed resource path. Its rel set is structurally fixed,
/// so caching it for the life of a command is allowed (ADR 0557); nothing else is cached, because content
/// never is.
/// </para>
/// <para>
/// <b>Following costs one read, not one per rel.</b> A caller that needs two rels from the same resource
/// reads it once and follows both from the response — which is why <see cref="LinksOfAsync"/> hands back the
/// whole map rather than resolving a single rel at a time.
/// </para>
/// </remarks>
public sealed class Hypermedia(SimplArchiveApi api)
{
    private IReadOnlyDictionary<string, string>? _rootLinks;

    /// <summary>The href the API root advertises for <paramref name="rel"/>.</summary>
    /// <remarks>
    /// <c>"api"</c> is written INLINE rather than held in a constant, on purpose. It is the one address this
    /// tool is allowed to know, and `ClientHypermediaTests` counts it here — a constant would hide it from the
    /// guard, which is exactly the shape that let the web client's entry-point budget be satisfied by a string
    /// inside a COMMENT for months (#862). The exception is counted, never exempted.
    /// </remarks>
    public async Task<string> RootHrefAsync(string rel, CancellationToken cancellationToken)
    {
        _rootLinks ??= Links(await api.GetAsync("api", cancellationToken));

        return Href(_rootLinks, rel, "the API root");
    }

    /// <summary>Reads a resource and returns everything it advertises.</summary>
    public async Task<IReadOnlyDictionary<string, string>> LinksOfAsync(
        string href, CancellationToken cancellationToken) => Links(await api.GetAsync(href, cancellationToken));

    /// <summary>The href for <paramref name="rel"/>, or a refusal naming what was missing.</summary>
    /// <remarks>
    /// A missing rel means "not available to you, here, now" (ADR 0543) — it is as likely to be a right the
    /// caller does not hold as a version that does not offer the feature, so the message says both rather
    /// than guessing.
    /// </remarks>
    public static string Href(IReadOnlyDictionary<string, string> links, string rel, string source) =>
        links.TryGetValue(rel, out var href)
            ? href
            : throw new CliException(
                $"{source} does not offer '{rel}'. Either this installation does not provide it, "
                + "or the signed-in principal may not use it.");

    private static IReadOnlyDictionary<string, string> Links(JsonElement resource)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);

        if (resource.TryGetProperty("links", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in array.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel) && link.TryGetProperty("href", out var href)
                    && rel.GetString() is { } name && href.GetString() is { } address)
                {
                    // First wins: a resource may advertise one rel on several methods at one address
                    // (ADR 0719), and they are the same address.
                    links.TryAdd(name, address);
                }
            }
        }

        return links;
    }
}
