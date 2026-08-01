using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

// "Is this our server?" — an unauthenticated probe of a URL's API root (ADR "Desktop tenant configuration",
// issue #270). GETs `<url>/api` and confirms the response is SimplArchive's own HATEOAS discovery document
// (ADR "API discoverability / root endpoint design") rather than merely reachable. Any failure / non-SimplArchive
// response is a plain false. Used by the tenant manager's live URL validation to tint a correct URL light green.
public static class ServerIdentity
{
    public static async Task<bool> IsSimplArchiveAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api", cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            return LooksLikeApiRoot(json);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The discovery document is our root iff it carries a `links` array with the self link to `/api` plus the
    // `openIdConfiguration` link — a shape generic "reachable JSON" won't have, so a foreign server can't match.
    internal static bool LooksLikeApiRoot(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var hasSelf = false;
            var hasOidc = false;
            foreach (var link in links.EnumerateArray())
            {
                var rel = link.TryGetProperty("rel", out var r) ? r.GetString() : null;
                var href = link.TryGetProperty("href", out var h) ? h.GetString() : null;
                if (rel == "self" && href == "/api")
                {
                    hasSelf = true;
                }

                if (rel == "openIdConfiguration")
                {
                    hasOidc = true;
                }
            }

            return hasSelf && hasOidc;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
