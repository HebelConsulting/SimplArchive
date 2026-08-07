using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    public async Task A_folder_cannot_be_shared()
    {
        var (api, _, _) = await SeedShareableDocumentAsync();
        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Folder share {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "a-folder" })).GetProperty("id").GetGuid();

        var response = await api.PostAsJsonAsync($"/api/documents/{folderId}/external-links", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("CANNOT_SHARE_FOLDER", problem.GetProperty("errorCode").GetString());
    }

    // Extension is measured from TODAY, so a link with time left does not accumulate it (ADR 0546).
    [Fact]
    public async Task Extending_measures_from_today_not_from_the_current_expiry()
    {
        var (api, _, docId) = await SeedShareableDocumentAsync();

        var created = await PostJson(api, $"/api/documents/{docId}/external-links",
            new { expiresAt = DateTimeOffset.UtcNow.AddDays(20) });
        var linkId = created.GetProperty("id").GetGuid();

        var extend = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/external-links/{linkId}/expiry")
        {
            Content = JsonContent.Create(new { days = 90 }),
        };
        extend.Headers.TryAddWithoutValidation("If-Match", created.GetProperty("etag").GetString());
        (await api.SendAsync(extend)).EnsureSuccessStatusCode();

        var listed = (await GetJson(api, $"/api/documents/{docId}/external-links"))
            .GetProperty("externalLinks").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == linkId);

        // 90 days from now — NOT 110 (20 remaining + 90).
        var expires = listed.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.True(expires < DateTimeOffset.UtcNow.AddDays(91), $"expected ~90 days out, got {expires:u}");
        Assert.True(expires > DateTimeOffset.UtcNow.AddDays(89), $"expected ~90 days out, got {expires:u}");
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

    // Seeds a tenant with the feature switched ON, a service account holding the right, and a confirmed document.
    private async Task<(HttpClient Api, Guid TenantId, Guid DocumentId)> SeedShareableDocumentAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        await SetAllowExternalLinksAsync(tenantId, true);
        await GrantCreateExternalLinkAsync(clientId);

        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
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

    private async Task GrantCreateExternalLinkAsync(string clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var account = await db.ServiceAccounts.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(s => s.OpenIddictApplicationClientId == clientId);
        account.CanCreateExternalLink = true;
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
