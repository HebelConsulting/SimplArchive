using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The external-sharing-links area (#443, ops tranche, ADR 0546): the admin listing, the caller's own
/// links, create/revoke/renew and the deliberate reveal. Rides the shared authenticated <see cref="ApiCore"/>.
/// </summary>
public sealed class ExternalLinksClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    // ---- External links (ADR 0546) ---------------------------------------------------------------------

    public sealed record ExternalLinkInfo(
        Guid Id, Guid DocumentId, string DocumentName, string? Url, DateTimeOffset ExpiresAt,
        int? MaxAccesses, int AccessCount, string CreatedByName, bool CanExtend, string Etag,
        string? RevokeHref, string? AvailabilityHref, Guid? ParentId, string? RevealUrlHref = null,
        string? DocumentHref = null, string? ParentHref = null)
    {
        /// <summary>
        /// The expiry as the READER experiences it. The server sends UTC; the row used to format that raw while
        /// the detail dialog called ToLocalTime(), so one link showed two times an hour apart outside UTC — the
        /// list said 20:40 and its own detail said 21:40 (the bug report this fixes). One property now, used by
        /// both, so they cannot drift again.
        /// </summary>
        public string ExpiresLocal => ExpiresAt.ToLocalTime().ToString("g");
    }

    public sealed record ExternalLinkListInfo(IReadOnlyList<ExternalLinkInfo> Links, bool CanCreate, bool CanViewOthers);

    // Follows the href the document resource advertised via its "external-links" rel (ADR 0543) — never composed.
    public async Task<ExternalLinkListInfo> GetExternalLinksAsync(string linksHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.GetAsync(linksHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ExternalLinkListInfo([], false, false);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseLinkList(doc.RootElement);
    }

    // The caller's own links across all documents; a tenant admin may filter by user or group.
    public async Task<ExternalLinkListInfo> GetMyExternalLinksAsync(
        string linksHref, Guid? userId = null, Guid? groupId = null, CancellationToken cancellationToken = default)
    {
        var query = groupId is { } g ? $"?groupId={g}" : userId is { } u ? $"?userId={u}" : "";
        return await GetExternalLinksAsync(linksHref + query, cancellationToken);
    }

    // Returns the created link — the ONLY time its URL is available, since the list endpoints never return the
    // token (ADR 0546). Null when the tenant has the feature switched off or the caller lacks the right.
    // Returns null ONLY when the share was refused — the tenant switch is off, or the caller lacks the right.
    // Anything else throws, so the dialog reports a real failure as one. Collapsing every non-success into null
    // is what made a 500 (a non-UTC expiry Postgres refused to store) display as "external links are switched off
    // for this tenant": a message that sent the reader to a setting that was already correct.
    public async Task<ExternalLinkInfo?> CreateExternalLinkAsync(
        string linksHref, DateTimeOffset? expiresAt, int? maxAccesses, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(linksHref, new { expiresAt, maxAccesses }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return null;
            }

            throw new ApiActionException($"The link could not be created ({(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseLink(doc.RootElement);
    }

    public async Task<bool> RevokeExternalLinkAsync(string revokeHref, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, revokeHref);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _core.Http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // Days are measured from TODAY by the server, not added onto what remains, and the access cap travels in the
    // same call — a link out of both time and accesses is only half-renewed by moving either alone (ADR 0546).
    // maxAccesses null means unlimited; the server takes both in one request so they cannot land apart.
    public async Task<bool> RenewExternalLinkAsync(
        string availabilityHref, int days, int? maxAccesses, string etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, availabilityHref)
        {
            Content = JsonContent.Create(new { days, maxAccesses }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _core.Http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static ExternalLinkListInfo ParseLinkList(JsonElement root)
    {
        var links = new List<ExternalLinkInfo>();
        if (root.TryGetProperty("externalLinks", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            links.AddRange(items.EnumerateArray().Select(ParseLink));
        }

        return new ExternalLinkListInfo(
            links,
            root.TryGetProperty("canCreate", out var c) && c.ValueKind == JsonValueKind.True,
            root.TryGetProperty("canViewOthers", out var v) && v.ValueKind == JsonValueKind.True);
    }

    private static ExternalLinkInfo ParseLink(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.TryGetProperty("documentId", out var d) ? d.GetGuid() : Guid.Empty,
        item.TryGetProperty("documentName", out var dn) ? dn.GetString() ?? "" : "",
        item.TryGetProperty("url", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() : null,
        item.GetProperty("expiresAt").GetDateTimeOffset(),
        item.TryGetProperty("maxAccesses", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetInt32() : null,
        item.TryGetProperty("accessCount", out var a) ? a.GetInt32() : 0,
        item.TryGetProperty("createdByName", out var cb) ? cb.GetString() ?? "" : "",
        item.TryGetProperty("canExtend", out var ce) && ce.ValueKind == JsonValueKind.True,
        item.TryGetProperty("etag", out var e) ? e.GetString() ?? "" : "",
        ApiCore.RelHref(item, "revoke"),
        ApiCore.RelHref(item, "availability"),
        // Null in the per-document list, which is already sitting on the document — "Go to" only means something
        // in the cross-document one, where a row is the reader's only handle on where the thing lives.
        item.TryGetProperty("parentId", out var pid) && pid.ValueKind != JsonValueKind.Null ? pid.GetGuid() : null,
        // The tenant's opt-in to revealing an existing link's URL (issue #412), as the server states it: the rel
        // is advertised only where ShowExternalLinkUrl is on, so its ABSENCE is what makes "not shown" truthful
        // (ADR 0543). The desktop ignored it entirely and always claimed the URL was unavailable.
        ApiCore.RelHref(item, "reveal-url"),
        // The addresses "Go to" follows (#443): the document's own, and its parent's where it has one.
        ApiCore.RelHref(item, "document"),
        ApiCore.RelHref(item, "parent"));

    /// <summary>
    /// An existing link's URL, fetched on demand by FOLLOWING the row's advertised <c>reveal-url</c> (ADR 0543).
    /// </summary>
    /// <remarks>
    /// Deliberately not carried by the listing: the token travels only when somebody asks for this one link,
    /// which is what keeps it out of the page every row arrived on. Null when the fetch fails, so the caller
    /// leaves the note as it was rather than showing an empty "URL:".
    /// </remarks>
    public async Task<string?> RevealExternalLinkUrlAsync(string revealHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.GetAsync(revealHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
    }
}
