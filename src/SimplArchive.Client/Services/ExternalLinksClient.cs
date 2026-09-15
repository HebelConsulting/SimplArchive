using System.Net.Http.Json;

namespace SimplArchive.Client.Services;

/// <summary>
/// The external-link WRITES, and the <c>If-Match</c> that goes with each of them (#1218).
/// </summary>
/// <remarks>
/// <para>
/// The mirror of the desktop's <c>ExternalLinksClient</c>, which ADR 0511 makes the reference: there, revoking
/// and renewing are two methods that own their ETag, and no view composes the request. Here they were written
/// out in the dialogs, and it had already cost what a copied helper always costs — <b>revoke existed twice</b>
/// (the document's links dialog and "my external links") and <b>renew existed twice</b> (the list's 90-day
/// button and the detail dialog's chosen span), four sites for two operations. A fifth copy was one new
/// share surface away.
/// </para>
/// <para>
/// The ETag is the point. Both routes call <c>RequireIfMatch</c> server-side, so a dropped header is a 428 and
/// a stale one a 412 — loud rather than silent, which is a mercy, but "loud at runtime" is not the same as
/// "caught before release", and nothing exercised these paths at all (#1218). Keeping the header in one place
/// per operation is what stops the next dialog from re-deriving it and getting it subtly wrong.
/// </para>
/// <para>
/// Takes <c>href</c> and <c>etag</c> as plain values rather than a row type: each dialog carries its own
/// private response record, the hrefs are rel-supplied (ADR 0543) and the tag travels with the row it came from
/// (ADR 0557). Passing the two values keeps this free of the dialogs' shapes — and matches the desktop
/// signature, so the two clients stay readable side by side.
/// </para>
/// </remarks>
public sealed class ExternalLinksClient(HttpClient http)
{
    /// <summary>Revokes a link via its <c>revoke</c> rel. False when the server refused.</summary>
    public async Task<bool> RevokeAsync(string revokeHref, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, revokeHref);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Renews a link via its <c>availability</c> rel: <paramref name="days"/> measured from TODAY by the server
    /// rather than added onto what remains (ADR 0546), with the access cap travelling in the SAME request.
    /// </summary>
    /// <remarks>
    /// The cap is not optional-by-omission. Availability replaces both values, so leaving it out quietly turns
    /// a capped link into an unlimited one — which is why it is a required parameter here rather than something
    /// a caller may forget. Null means unlimited, deliberately, and only when the caller says so.
    /// </remarks>
    public async Task<bool> RenewAsync(
        string availabilityHref, int days, int? maxAccesses, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, availabilityHref)
        {
            Content = JsonContent.Create(new { days, maxAccesses }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
