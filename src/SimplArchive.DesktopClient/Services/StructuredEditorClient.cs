using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The read/save plumbing behind the structured contact and appointment editors (#564, ADR 0631).
/// </summary>
/// <remarks>
/// One implementation with a type parameter rather than two near-identical clients: both editors do exactly the
/// same work — follow a rel off the document, GET the resource, keep its ETag, PUT it back under
/// <c>If-Match</c> — and only the payload shape differs. That difference arrives as a lambda at the call site,
/// which is where a reader wants both the difference and the delegation.
///
/// The address is always <b>followed</b>, never composed (ADR 0543): the caller passes the row's advertised
/// <c>self</c>, and the sub-resource comes from what that document offers. A row from a children listing
/// advertises what browsing needs and not this, so resolving it costs one request (ADR 0559) — taken once, with
/// every later save going to the href already in hand rather than re-resolving it.
/// </remarks>
public sealed class StructuredEditorClient(ApiCore core, DocumentsClient documents)
{
    /// <summary>What a read returned: the parsed value, where to save it, and the token to save it with.</summary>
    /// <param name="Value">The parsed resource.</param>
    /// <param name="Href">The resource's own address — saved back to this, not to a re-derived one.</param>
    /// <param name="ETag">The document's concurrency token, required on the way back as <c>If-Match</c>.</param>
    /// <param name="CanEdit">False when the caller may read but not save, so the form opens read-only.</param>
    public sealed record Loaded<T>(T Value, string Href, string ETag, bool CanEdit);

    /// <summary>
    /// Follows <paramref name="rel"/> off the document at <paramref name="documentSelfHref"/> and reads it.
    /// Returns null when the document does not advertise the rel — which is the server saying "not available
    /// to you, here, now" (ADR 0543), not an error to report.
    /// </summary>
    public async Task<Loaded<T>?> ReadAsync<T>(
        string documentSelfHref,
        string rel,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string> links;
        try
        {
            links = await documents.GetDocumentLinksAsync(documentSelfHref, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }

        if (!links.TryGetValue(rel, out var href))
        {
            return null;
        }

        using var response = await core.Http.GetAsync(href, cancellationToken);
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
    /// Creates an item by POSTing <paramref name="payload"/> to a collection's advertised create rel, and
    /// returns the created document's own address (#631).
    /// </summary>
    /// <remarks>
    /// No <c>If-Match</c>: there is nothing yet to collide with. The href is the one the COLLECTION advertised,
    /// so the caller composes nothing — and because the create takes the editor's whole resource, this is one
    /// request rather than a create followed by a save, which would leave a half-filled contact behind if the
    /// second one failed.
    /// </remarks>
    public async Task<Guid?> CreateAsync(
        string createHref, object payload, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsync(createHref, JsonContent.Create(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ApiCore.ThrowIfProblemAsync(response, "The entry could not be created.", cancellationToken);
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid) ? guid : null;
    }

    /// <summary>
    /// Saves <paramref name="payload"/> back to the address the read came from, under its ETag.
    /// </summary>
    public async Task SaveAsync(string href, object payload, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        using var response = await core.Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Surfaces the RFC 7807 detail when there is one — a 412 here means somebody else saved first, and
            // "the entry changed while you were editing" is the only useful thing to tell the user.
            await ApiCore.ThrowIfProblemAsync(response, "The entry could not be saved.", cancellationToken);
        }
    }
}
