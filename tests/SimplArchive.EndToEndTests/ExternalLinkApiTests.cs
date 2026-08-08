using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// External links (ADR 0546, issue #385) — the system's only anonymous content endpoint.
//
// The assertions that matter here are the NEGATIVE ones. A feature that hands documents to people without
// accounts is defined by what it refuses and by what it declines to reveal, so most of this file is about
// rejection paths being identical, the tenant kill switch actually killing, and the token never leaking into
// places that are read more widely than the create response.
[Collection(E2ECollection.Name)]
public class ExternalLinkApiTests
{
    private readonly E2EApiFactory _factory;

    public ExternalLinkApiTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_link_serves_the_current_version_to_an_anonymous_caller()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();

        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { });
        var url = created.GetProperty("url").GetString()!;
        Assert.Contains("/api/external-links/", url);

        // No credentials at all — a fresh client with no bearer token, which is the entire point.
        using var anonymous = _factory.CreateClient();
        var redeemed = await GetJson(anonymous, RelativePath(url));

        Assert.False(string.IsNullOrWhiteSpace(redeemed.GetProperty("downloadUrl").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(redeemed.GetProperty("fileName").GetString()));
    }

    // Every rejection must be INDISTINGUISHABLE, or the endpoint becomes an oracle telling an attacker which
    // tokens exist — which is exactly what makes guessing worthwhile.
    [Fact]
    public async Task Every_rejection_looks_the_same()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();

        // Exhausted: one access allowed, then spent.
        var exhausted = await PostJson(api, $"/api/documents/{docId}/external-links", new { maxAccesses = 1 });
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(RelativePath(exhausted.GetProperty("url").GetString()!))).EnsureSuccessStatusCode();

        // Revoked.
        var revoked = await PostJson(api, $"/api/documents/{docId}/external-links", new { });
        var revokedUrl = revoked.GetProperty("url").GetString()!;
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{docId}/external-links/{revoked.GetProperty("id").GetGuid()}");
        delete.Headers.TryAddWithoutValidation("If-Match", revoked.GetProperty("etag").GetString());
        (await api.SendAsync(delete)).EnsureSuccessStatusCode();

        var responses = new List<HttpResponseMessage>
        {
            await anonymous.GetAsync($"/api/external-links/{Guid.NewGuid():N}"),          // never existed
            await anonymous.GetAsync(RelativePath(exhausted.GetProperty("url").GetString()!)), // exhausted
            await anonymous.GetAsync(RelativePath(revokedUrl)),                            // revoked
        };

        var bodies = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.True(bodies.Distinct().Count() == 1,
            "an unknown, exhausted and revoked token must produce byte-identical responses:\n" + string.Join("\n", bodies));
    }

    // Switching the tenant setting off must stop links ALREADY OUT THERE, not merely block new ones — otherwise
    // an administrator reaching for it during a leak has not actually stopped anything (ADR 0546).
    [Fact]
    public async Task Switching_the_tenant_setting_off_kills_live_links()
    {
        var (api, tenantId, docId) = await SeedShareableDocumentAsync();

        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { });
        var path = RelativePath(created.GetProperty("url").GetString()!);

        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(path)).EnsureSuccessStatusCode();

        await SetAllowExternalLinksAsync(tenantId, false);

        Assert.Equal(HttpStatusCode.Gone, (await anonymous.GetAsync(path)).StatusCode);

        // And it is reversible — the link was retained, not destroyed.
        await SetAllowExternalLinksAsync(tenantId, true);
        (await anonymous.GetAsync(path)).EnsureSuccessStatusCode();
    }

    // The token is a live credential. It comes back exactly once, from the create call — a list endpoint is read
    // far more widely, so leaking it there would turn any listing into a set of working URLs.
    [Fact]
    public async Task The_token_is_returned_only_when_the_link_is_created()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();

        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { });
        var token = RelativePath(created.GetProperty("url").GetString()!).Split('/').Last();

        var listed = await GetJson(api, $"/api/documents/{docId}/external-links");
        var listJson = listed.GetRawText();

        Assert.DoesNotContain(token, listJson, StringComparison.Ordinal);

        var mine = await GetJson(api, "/api/external-links");
        Assert.DoesNotContain(token, mine.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_cannot_be_shared_and_does_not_advertise_the_rel()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();
        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Folder share {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "a-folder" })).GetProperty("id").GetGuid();

        var response = await api.PostAsJsonAsync($"/api/documents/{folderId}/external-links", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("CANNOT_SHARE_FOLDER", problem.GetProperty("errorCode").GetString());

        // And the rel is ABSENT, so no client draws a share button whose only outcome is that refusal — a missing
        // rel means "not available to you, here, now" (ADR 0543). The document beside it still has one, so this
        // asserts the distinction rather than the feature merely being off.
        Assert.DoesNotContain(await RelsAsync(api, folderId), rel => rel == "external-links");
        Assert.Contains(await RelsAsync(api, docId), rel => rel == "external-links");
    }

    private static async Task<List<string>> RelsAsync(HttpClient api, Guid documentId) =>
        (await GetJson(api, $"/api/documents/{documentId}")).GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString() ?? "").ToList();

    // Renewal is measured from TODAY, so a link with time left does not accumulate it (ADR 0546) — and it carries
    // the access cap, because "keep this share usable" is one decision: a link that has run out of both time and
    // accesses is only half-renewed by moving either alone.
    [Fact]
    public async Task Renewing_measures_from_today_and_replaces_the_access_cap()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();

        var created = await PostJson(api, $"/api/documents/{docId}/external-links",
            new { expiresAt = DateTimeOffset.UtcNow.AddDays(20), maxAccesses = 5 });
        var linkId = created.GetProperty("id").GetGuid();

        var renewed = await RenewAsync(api, docId, linkId, created.GetProperty("etag").GetString()!, 90, 20);
        renewed.EnsureSuccessStatusCode();

        var listed = await SingleLinkAsync(api, docId, linkId);

        // 90 days from now — NOT 110 (20 remaining + 90).
        var expires = listed.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.True(expires < DateTimeOffset.UtcNow.AddDays(91), $"expected ~90 days out, got {expires:u}");
        Assert.True(expires > DateTimeOffset.UtcNow.AddDays(89), $"expected ~90 days out, got {expires:u}");
        Assert.Equal(20, listed.GetProperty("maxAccesses").GetInt32());
    }

    // The cap moves in EITHER direction, and clears. Lowering is a tightening — the same direction as revoking —
    // so it needs no rule of its own; only zero and below are refused, since "a link nobody may open" is what
    // revoking is for and reads as a typo for unlimited.
    [Fact]
    public async Task The_access_cap_can_be_raised_lowered_or_cleared()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();

        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { maxAccesses = 5 });
        var linkId = created.GetProperty("id").GetGuid();
        var etag = created.GetProperty("etag").GetString()!;

        (await RenewAsync(api, docId, linkId, etag, 30, 2)).EnsureSuccessStatusCode();
        var lowered = await SingleLinkAsync(api, docId, linkId);
        Assert.Equal(2, lowered.GetProperty("maxAccesses").GetInt32());

        (await RenewAsync(api, docId, linkId, lowered.GetProperty("etag").GetString()!, 30, null)).EnsureSuccessStatusCode();
        var cleared = await SingleLinkAsync(api, docId, linkId);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("maxAccesses").ValueKind);

        var refused = await RenewAsync(api, docId, linkId, cleared.GetProperty("etag").GetString()!, 30, 0);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_EXTERNAL_LINK_MAX_ACCESSES", problem.GetProperty("errorCode").GetString());
    }

    private async Task<HttpResponseMessage> RenewAsync(HttpClient api, Guid docId, Guid linkId, string etag, int days, int? maxAccesses)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/external-links/{linkId}/availability")
        {
            Content = JsonContent.Create(new { days, maxAccesses }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await api.SendAsync(request);
    }

    private static async Task<JsonElement> SingleLinkAsync(HttpClient api, Guid docId, Guid linkId) =>
        (await GetJson(api, $"/api/documents/{docId}/external-links"))
            .GetProperty("externalLinks").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == linkId);

    // A client in a non-UTC timezone sends a valid instant carrying its own offset. Postgres stores instants with
    // offset 0 only, so the value used to reach Npgsql unchanged and blow up inside SaveChanges — a 500 from a
    // request that was never wrong. The desktop client, seeding its picker from DateTimeOffset.Now, hit this every
    // time outside UTC; nothing caught it because every test here (and CI) runs in UTC.
    //
    // +02:00 rather than "some non-zero offset" so the test states the case that actually failed, and the instant
    // is asserted preserved: normalising must not shift the expiry, only how it is written down.
    [Fact]
    public async Task An_expiry_sent_with_a_local_offset_is_stored_as_the_same_instant()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();

        var localExpiry = new DateTimeOffset(2026, 9, 6, 23, 59, 0, TimeSpan.FromHours(2));
        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { expiresAt = localExpiry });

        Assert.Equal(localExpiry.ToUniversalTime(), created.GetProperty("expiresAt").GetDateTimeOffset());

        // And the link genuinely works — the row was written, rather than the request failing at SaveChanges.
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(RelativePath(created.GetProperty("url").GetString()!))).EnsureSuccessStatusCode();
    }

    // The link's audience is a person with no account and no client — they paste it into a browser. Answering a
    // browser with the JSON resource showed them machine-readable text with a URL buried in it, which is not what
    // "the link opens the document" means to anybody. Accept decides: HTML for a browser, the resource for a
    // programmatic caller, one URL for both because that URL is what people pass around.
    [Fact]
    public async Task A_browser_gets_a_page_and_a_programmatic_caller_still_gets_json()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();
        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { });
        var path = RelativePath(created.GetProperty("url").GetString()!);

        using var anonymous = _factory.CreateClient();

        var browser = new HttpRequestMessage(HttpMethod.Get, path);
        browser.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        var page = await anonymous.SendAsync(browser);
        page.EnsureSuccessStatusCode();

        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("shared-doc", html, StringComparison.Ordinal);

        // The page carries NO storage URL: a presigned URL lives two minutes, so one baked in would be dead by
        // the time a person had read the page. Both buttons point at the content route, which mints one on click.
        Assert.DoesNotContain("X-Amz-", html, StringComparison.Ordinal);
        Assert.Contains("/content", html, StringComparison.Ordinal);

        // The recipient has no account and may never have heard of SimplArchive, so the footer names it, links
        // it, and says who is behind it (issue #411).
        Assert.Contains("https://www.simplarchive.dev", html, StringComparison.Ordinal);
        Assert.Contains("Hebel Consulting GmbH", html, StringComparison.Ordinal);

        // Asking for JSON explicitly still gets the resource, so an integration is not forced to scrape markup.
        var json = await GetJson(anonymous, path);
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("downloadUrl").GetString()));
    }

    // Taking delivery is the same redemption continuing, not a second one — counting the click as well would
    // silently halve every cap an administrator set.
    [Fact]
    public async Task Fetching_the_content_does_not_count_a_second_access()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();
        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { });
        var path = RelativePath(created.GetProperty("url").GetString()!);

        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(path)).EnsureSuccessStatusCode();

        // Redirects are not followed: the redirect itself is the assertion, and its target is object storage.
        using var noRedirect = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var content = await noRedirect.GetAsync($"{path}/content");
        Assert.Equal(HttpStatusCode.Redirect, content.StatusCode);
        Assert.Contains("X-Amz-", content.Headers.Location!.Query, StringComparison.Ordinal);

        // The two buttons differ ONLY in disposition, and that difference is the point: "Open document" must let
        // the browser render the file, "Download" must save it. The download presign hardcodes attachment, so
        // asking it for an inline view (what the old ?download=true switch did) saved the file either way.
        Assert.Contains("inline", Uri.UnescapeDataString(content.Headers.Location!.Query), StringComparison.Ordinal);

        var asDownload = await noRedirect.GetAsync($"{path}/content?download=true");
        Assert.Contains("attachment", Uri.UnescapeDataString(asDownload.Headers.Location!.Query), StringComparison.Ordinal);

        // "inline" alone does not open anything: objects are stored as application/octet-stream, and no browser
        // renders one of those — it downloads it, disposition notwithstanding. The inline URL must therefore also
        // OVERRIDE the content type, or Open is a second Download button with a different label.
        Assert.Contains("response-content-type=text/plain",
            Uri.UnescapeDataString(content.Headers.Location!.Query), StringComparison.Ordinal);

        var listed = (await GetJson(api, $"/api/documents/{docId}/external-links"))
            .GetProperty("externalLinks").EnumerateArray().Single();
        Assert.Equal(1, listed.GetProperty("accessCount").GetInt32());
    }

    // A dead link reaches the least equipped reader in the system — someone outside it, holding a URL that no
    // longer works. They get a sentence, not a problem document.
    [Fact]
    public async Task A_browser_opening_a_dead_link_gets_a_readable_page()
    {
        using var anonymous = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/external-links/{Guid.NewGuid():N}");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no longer available", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // The per-document rows must advertise the renewal rel, not leave the client to build the URL. They did not,
    // so that dialog composed ".../expiry" itself — and went on calling it after the route became
    // ".../availability", failing every extend with a message about WebDAV passwords. A rel exists to make a route
    // move invisible to clients; this is what it costs when one is missing.
    //
    // The second assertion is the trap behind it: availability REPLACES both halves, so a renewal that omits
    // maxAccesses turns a capped link into an unlimited one. Pinned here because it is silent, and because the
    // client that forgets it produces no error at all — just a share that outlives its limit.
    [Fact]
    public async Task A_documents_links_advertise_renewal_and_renewal_replaces_the_cap()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();
        var created = await PostJson(api, $"/api/documents/{docId}/external-links", new { maxAccesses = 5 });
        var linkId = created.GetProperty("id").GetGuid();

        var listed = await SingleLinkAsync(api, docId, linkId);
        var rels = listed.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToList();
        Assert.Contains("availability", rels);
        Assert.Contains("revoke", rels);

        // Renewing WITHOUT a cap clears it — the caller said "unlimited" by omission.
        (await RenewAsync(api, docId, linkId, listed.GetProperty("etag").GetString()!, 90, null))
            .EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await SingleLinkAsync(api, docId, linkId)).GetProperty("maxAccesses").ValueKind);
    }

    // A caller without CanCreateExternalLink may read the document but not publish it to strangers.
    [Fact]
    public async Task Creating_requires_the_dedicated_right()
    {
        var (_, tenantId, docId) = await SeedShareableDocumentAsync();

        var email = $"no-share-{Guid.NewGuid():N}@e2e.local";
        const string password = "share1234";
        await _factory.SeedUserAsync(tenantId, email, password, "No Share", canManageRepositories: true);
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var response = await reader.PostAsJsonAsync($"/api/documents/{docId}/external-links", new { });
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"expected the share to be refused, got {response.StatusCode}");
    }

    // Seeds a tenant with the feature switched ON, a USER holding the right, and a confirmed document.
    //
    // A user rather than a service account, deliberately: only a person can share (ADR 0546), so a service-account
    // fixture here would be testing a path the product no longer has. Tenant admin so the ACL side is satisfied
    // without building an entry per test — the right being separately required is what
    // Creating_requires_the_dedicated_right pins, using a non-admin who can read.
    private async Task<(HttpClient Api, Guid TenantId, Guid DocumentId)> SeedShareableDocumentAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        await SetAllowExternalLinksAsync(tenantId, true);

        var email = $"sharer-{Guid.NewGuid():N}@e2e.local";
        const string password = "share1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Sharer",
            canManageRepositories: true, canCreateExternalLink: true, isTenantAdmin: true);

        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Share {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "shared-doc" })).GetProperty("id").GetGuid();

        var version = await PostJson(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("shared content")))).EnsureSuccessStatusCode();
        }

        (await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { }))
            .EnsureSuccessStatusCode();

        return (api, tenantId, docId);
    }

    private async Task SetAllowExternalLinksAsync(Guid tenantId, bool allow)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(t => t.Id == tenantId);
        tenant.AllowExternalLinks = allow;
        await db.SaveChangesAsync();
    }

    private static string RelativePath(string url) => new Uri(url).PathAndQuery;

    private static async Task<JsonElement> PostJson(HttpClient api, string url, object body)
    {
        var response = await api.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GetJson(HttpClient api, string url)
    {
        var response = await api.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
}
