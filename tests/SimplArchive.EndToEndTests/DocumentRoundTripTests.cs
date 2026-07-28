using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API (in-process) + real Postgres + MinIO (ADR "Container-backed end-to-end
// integration tests"). Drives the actual HTTP layer, EF migrations, OpenIddict tokens, and object storage.
[Collection(E2ECollection.Name)]
public class DocumentRoundTripTests
{
    private readonly E2EApiFactory _factory;

    public DocumentRoundTripTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Full_document_round_trip_over_real_postgres_and_minio()
    {
        var api = await AuthedClientAsync(canManageRepositories: true);

        var repoId = (await PostJson(api, "/api/repositories", new { name = "E2E Repo" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "roundtrip" })).GetProperty("id").GetGuid();

        // Create a version → presigned upload URL, then PUT the bytes straight to MinIO (the Api never proxies).
        var created = await PostJson(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        var uploadUrl = created.GetProperty("uploadUrl").GetString()!;

        var content = Encoding.UTF8.GetBytes("hello end-to-end world");
        using var storage = new HttpClient();
        (await storage.PutAsync(uploadUrl, new ByteArrayContent(content))).EnsureSuccessStatusCode();

        // Finalize (server re-fetches + hashes the object) → Confirmed with a version number.
        var finalized = await PutJson(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        Assert.Equal("Confirmed", finalized.GetProperty("status").GetString());
        Assert.Equal(1, finalized.GetProperty("versionNumber").GetInt32());

        // The confirmed version exposes a `download` presigned link; download and verify byte-for-byte.
        var version = await GetJson(api, $"/api/documents/{docId}/versions/{versionId}");
        var downloadUrl = Link(version, "download");
        Assert.NotNull(downloadUrl);

        var downloaded = await storage.GetByteArrayAsync(downloadUrl!);
        Assert.Equal(content, downloaded);
    }

    [Fact]
    public async Task Finalize_is_idempotent()
    {
        var api = await AuthedClientAsync(canManageRepositories: true);
        var repoId = (await PostJson(api, "/api/repositories", new { name = "E2E Idempotent" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "idem" })).GetProperty("id").GetGuid();
        var created = await PostJson(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();

        using var storage = new HttpClient();
        (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();

        var first = await PutJson(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        var second = await PutJson(api, $"/api/documents/{docId}/versions/{versionId}", new { });

        Assert.Equal(first.GetProperty("versionNumber").GetInt32(), second.GetProperty("versionNumber").GetInt32());
        Assert.Equal(first.GetProperty("sha256Hash").GetString(), second.GetProperty("sha256Hash").GetString());
        Assert.Equal("Confirmed", second.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejects_unauthenticated_requests()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/repositories");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_creating_a_repository_without_the_right()
    {
        var api = await AuthedClientAsync(canManageRepositories: false);
        var response = await api.PostAsJsonAsync("/api/repositories", new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Returns_404_for_a_missing_document()
    {
        var api = await AuthedClientAsync(canManageRepositories: true);
        var response = await api.GetAsync($"/api/documents/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync(bool canManageRepositories)
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories);
        return _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body) =>
        await ReadJson(await client.PostAsJsonAsync(url, body));

    private static async Task<JsonElement> PutJson(HttpClient client, string url, object body) =>
        await ReadJson(await client.PutAsJsonAsync(url, body));

    private static async Task<JsonElement> GetJson(HttpClient client, string url) =>
        await ReadJson(await client.GetAsync(url));

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static string? Link(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links))
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.GetProperty("rel").GetString() == rel)
            {
                return link.GetProperty("href").GetString();
            }
        }

        return null;
    }
}
