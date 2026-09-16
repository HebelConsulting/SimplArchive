using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// A resource's advertised links, as the server stated them: rel → address <b>and method</b>.
/// </summary>
/// <remarks>
/// <para>
/// ADR 0543 makes rel names the compatibility surface, and #1173 proved the address half at scale — fourteen
/// routes moved and no client learned a new address. The <b>method</b> half was never true. A link carries a
/// <c>Method</c>, this client discarded it at the parser, and every call site named the verb itself, so a
/// route that kept its rel and changed only its verb still broke the client. ADR 0797 changed six methods and
/// five call sites answered 405 (#1192).
/// </para>
/// <para>
/// <b>Why this exists when <see cref="ApiCore.RelLink"/> already reads both.</b> That reads from the raw
/// <see cref="JsonElement"/>, so it can only serve a caller that still holds the response. The sites that
/// actually name verbs do not: <c>AddLegalHoldItemAsync(LegalHoldInfo hold, …)</c> takes a PARSED ROW, asks it
/// for an href and then names <c>PostAsJsonAsync</c> — the JSON is long gone. Every one of the 42 sites in the
/// burn-down ledger has that shape, which is why the ledger had not moved: the existing helper could not reach
/// them. Carrying the method on the row is what makes sending at a rel possible from a row.
/// </para>
/// <para>
/// <b>It also collapses a duplication.</b> Nineteen row records each carried their own byte-identical
/// <c>Href(rel)</c> — the null check and the lookup, written out nineteen times. That logic now lives here
/// once; the records keep a one-line forwarder so the 149 existing <c>row.Href(rel)</c> call sites are
/// untouched (a C# default interface member would not be visible on the concrete record, so an interface
/// cannot remove those forwarders without rewriting all 149).
/// </para>
/// <para>
/// <b>An href is stored exactly as <see cref="ApiCore"/> read it</b> — a leading slash trimmed so it composes
/// against the client's BaseAddress, absolute URLs untouched.
/// </para>
/// </remarks>
public sealed class LinkMap
{
    private readonly Dictionary<string, (string Href, string? Method)> _links;

    private LinkMap(Dictionary<string, (string Href, string? Method)> links) => _links = links;

    /// <summary>The address advertised for <paramref name="rel"/>, or null when it was not advertised.</summary>
    /// <remarks>
    /// Null is meaningful rather than missing data: a rel the server did not advertise means "not available to
    /// you, here, now" (ADR 0543), so a caller hides the affordance instead of trying and handling a refusal.
    /// </remarks>
    public string? Href(string rel) => _links.TryGetValue(rel, out var link) ? link.Href : null;

    /// <summary>The method the server advertised for <paramref name="rel"/>, or null.</summary>
    /// <remarks>
    /// Null here means the rel was absent OR carried no method. Both are a refusal to guess: see
    /// <see cref="ApiCore.SendRelAsync(LinkMap?, string, object?, CancellationToken)"/>, which throws rather
    /// than defaulting to POST — guessing is how a client comes to write where the server meant it to read.
    /// </remarks>
    public string? Method(string rel) => _links.TryGetValue(rel, out var link) ? link.Method : null;

    /// <summary>Whether the resource advertised <paramref name="rel"/> at all.</summary>
    public bool Has(string rel) => _links.ContainsKey(rel);

    /// <summary>The advertised rel names, for a caller rendering server-labelled generic actions (ADR 0743).</summary>
    public IEnumerable<string> Rels => _links.Keys;

    /// <summary>Alias of <see cref="Rels"/>, for callers that read it as a map.</summary>
    public IEnumerable<string> Keys => _links.Keys;

    /// <summary>
    /// No links at all — for an envelope whose record requires a map but whose response advertised none.
    /// </summary>
    /// <remarks>
    /// Distinct from a null <see cref="LinkMap"/> only in who is asserting: null means "this resource carries
    /// no links", while a caller reaching for Empty is saying "my type needs a map and there was nothing in
    /// it". Both answer every <see cref="Href"/> with null, which is the answer that matters.
    /// </remarks>
    public static readonly LinkMap Empty = new(new Dictionary<string, (string, string?)>(StringComparer.Ordinal));

    /// <summary>
    /// A map from rel → href alone, for a caller that assembled one itself and knows no methods.
    /// </summary>
    /// <remarks>
    /// Every <see cref="Method"/> answers null here, so <c>SendRelAsync</c> refuses rather than guessing. That
    /// is the honest outcome: these links came from somewhere that never carried a verb.
    /// </remarks>
    public static LinkMap FromHrefs(IReadOnlyDictionary<string, string> hrefs) =>
        new(hrefs.ToDictionary(kv => kv.Key, kv => (kv.Value, (string?)null), StringComparer.Ordinal));

    /// <summary>
    /// Reads a resource's <c>links</c> array. Returns <c>null</c> when it advertised none — meaningful, not an
    /// empty map by accident: it means no action is available here.
    /// </summary>
    public static LinkMap? From(JsonElement item)
    {
        if (!item.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var map = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var l in links.EnumerateArray())
        {
            if (l.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } r
                && l.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
            {
                map[r] = (h.TrimStart('/'), l.TryGetProperty("method", out var m) ? m.GetString() : null);
            }
        }

        return map.Count == 0 ? null : new LinkMap(map);
    }
}
