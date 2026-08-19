using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.Client.Services;

/// <summary>
/// The read/create/save plumbing behind the web structured contact and appointment editors (#631).
/// </summary>
/// <remarks>
/// <para>
/// The web twin of the desktop's <c>StructuredEditorClient</c>, and deliberately the same shape: the desktop is
/// the reference client (ADR 0511), and two surfaces that reach the same endpoints by different routes drift
/// until only one of them is right. One implementation with a type parameter, because both editors do exactly
/// the same work — follow a rel off the document, GET the resource, keep its ETag, PUT it back under
/// <c>If-Match</c> — and only the payload shape differs, which arrives as a lambda at the call site.
/// </para>
/// <para>
/// Every address is <b>followed</b>, never composed (ADR 0543). A row from a children listing advertises what
/// browsing needs and not the editor's sub-resource, so resolving it costs one request (ADR 0557) — taken once,
/// with the save going to the href already in hand.
/// </para>
/// </remarks>
public sealed class StructuredEditors
{
    private readonly HttpClient _http;

    public StructuredEditors(HttpClient http) => _http = http;

    /// <summary>What a read returned: the parsed form, where to save it, and the token to save it with.</summary>
    /// <param name="Value">The parsed resource.</param>
    /// <param name="Href">The resource's own address — saved back to this, not to a re-derived one.</param>
    /// <param name="ETag">The document's concurrency token, required on the way back as <c>If-Match</c>.</param>
    /// <param name="CanEdit">False when the caller may read but not save, so the form opens read-only.</param>
    public sealed record Loaded<T>(T Value, string Href, string ETag, bool CanEdit);

    /// <summary>
    /// Follows <paramref name="rel"/> off the document at <paramref name="documentHref"/> and reads it. Null
    /// when the document does not advertise the rel — the server saying "not available to you, here, now"
    /// (ADR 0543), which disables the affordance rather than producing a failed request.
    /// </summary>
    public async Task<Loaded<T>?> ReadAsync<T>(
        string documentHref, string rel, Func<JsonElement, T> parse, CancellationToken cancellationToken = default)
    {
        JsonElement document;
        try
        {
            document = await _http.GetFromJsonAsync<JsonElement>(documentHref, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        if (HrefOf(document, rel) is not { } href)
        {
            return null;
        }

        using var response = await _http.GetAsync(href, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // The ETag is the DOCUMENT's token, and the save needs it verbatim — quotes included, since that is
        // what If-Match compares against.
        var etag = response.Headers.ETag?.Tag ?? string.Empty;
        var canEdit = body.TryGetProperty("canEdit", out var flag) && flag.GetBoolean();
        return new Loaded<T>(parse(body), href, etag, canEdit);
    }

    /// <summary>
    /// Creates an item by POSTing to a collection's advertised create rel; returns its id (#631).
    /// </summary>
    /// <remarks>
    /// One request, not a create followed by a save: the endpoint takes the editor's whole resource, so nothing
    /// the user typed is left for a second call that could fail and leave a half-filled contact behind — and
    /// nothing exists at all until Save, so a cancelled dialog leaves no stub for a DAV client to sync.
    /// </remarks>
    public async Task<Guid?> CreateAsync(string createHref, object payload, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(createHref, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid) ? guid : null;
    }

    /// <summary>Saves <paramref name="payload"/> back to the address the read came from, under its ETag.</summary>
    /// <returns>True on success; false leaves the caller to report it — a 412 means somebody saved first.</returns>
    public async Task<bool> SaveAsync(
        string href, object payload, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static string? HrefOf(JsonElement resource, string rel) =>
        resource.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray()
                .Where(l => l.TryGetProperty("rel", out var r) && r.GetString() == rel)
                .Select(l => l.GetProperty("href").GetString())
                .FirstOrDefault()
            : null;
}
