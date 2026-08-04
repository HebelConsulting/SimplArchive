using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// Generic external-system import support (ADR 0520): a CanImport caller can stamp a document's origin key + resolve
// by it (idempotent re-import), and can create a version with a historical filing date that drives the object-key
// year (tenants/{t}/{filingYear}/…) + CreatedAt. Backdating without CanImport is refused. Not tied to any
// specific source system.
[Collection(E2ECollection.Name)]
public class OriginKeyAndFilingDateTests
{
    private readonly E2EApiFactory _factory;

    public OriginKeyAndFilingDateTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Origin_key_can_be_set_resolved_and_cleared_by_a_CanImport_caller()
    {
        // A service account (canManageRepositories → full ACL on its repos, but NO CanImport) owns the content.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var sa = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A tenant admin WITH CanImport (GrantTenantAdminAsync grants IsTenantAdmin + CanImport).
        var importerEmail = $"imp-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, importerEmail, "imp-1234", "Importer");
        await _factory.GrantTenantAdminAsync(importerEmail);
        using var importer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(importerEmail, "imp-1234"));

        var repoId = (await TestJson.Post(sa, "/api/repositories", new { name = $"ORG {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(sa, $"/api/documents/{repoId}/children", new { name = "origin-doc" })).GetProperty("id").GetGuid();

        var originTenantId = Guid.NewGuid();
        var originDocumentId = Guid.NewGuid();

        // The SA (no CanImport) may NOT set an origin key.
        Assert.Equal(HttpStatusCode.Forbidden, (await SetOriginAsync(sa, docId, originTenantId, originDocumentId)).StatusCode);

        // The importer sets it (200), and can then resolve the document by that origin key.
        Assert.Equal(HttpStatusCode.OK, (await SetOriginAsync(importer, docId, originTenantId, originDocumentId)).StatusCode);

        var got = await TestJson.Get(importer, $"/api/documents/{docId}/origin");
        Assert.Equal(originTenantId, got.GetProperty("originTenantId").GetGuid());
        Assert.Equal(originDocumentId, got.GetProperty("originDocumentId").GetGuid());

        var resolved = await TestJson.Get(importer, $"/api/documents/by-origin/{originTenantId}/{originDocumentId}");
        Assert.Equal(docId, resolved.GetProperty("id").GetGuid());

        // Clearing it (204) makes the origin unresolvable (404).
        var etag = (await importer.GetAsync($"/api/documents/{docId}/origin")).Headers.ETag!.Tag;
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{docId}/origin");
        del.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.NoContent, (await importer.SendAsync(del)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await importer.GetAsync($"/api/documents/by-origin/{originTenantId}/{originDocumentId}")).StatusCode);
    }

    [Fact]
    public async Task Filing_date_drives_the_bucket_year_and_requires_CanImport_to_backdate()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true); // no CanImport
        using var sa = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var importerEmail = $"imp-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, importerEmail, "imp-1234", "Importer");
        await _factory.GrantTenantAdminAsync(importerEmail);
        using var importer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(importerEmail, "imp-1234"));

        var repoId = (await TestJson.Post(sa, "/api/repositories", new { name = $"FIL {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(sa, $"/api/documents/{repoId}/children", new { name = "filing-doc" })).GetProperty("id").GetGuid();

        // The CanImport caller creates a version filed in 2003 → the object key (in the presigned upload URL) is
        // under …/2003/… and the stored CreatedAt is 2003.
        var created = await TestJson.Post(importer, $"/api/documents/{docId}/versions", new { fileExtension = ".txt", filedAt = "2003-05-01T00:00:00Z" });
        var uploadUrl = created.GetProperty("uploadUrl").GetString()!;
        Assert.Contains($"/{tenantId}/2003/", uploadUrl);

        // Finalize it, then the version's CreatedAt reflects 2003.
        var versionId = created.GetProperty("id").GetGuid();
        using var storage = new HttpClient();
        (await storage.PutAsync(uploadUrl, new ByteArrayContent(Encoding.UTF8.GetBytes("filed in 2003")))).EnsureSuccessStatusCode();
        await TestJson.Put(importer, $"/api/documents/{docId}/versions/{versionId}", new { });
        var version = (await TestJson.Get(importer, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray()
            .Single(v => v.GetProperty("id").GetGuid() == versionId);
        Assert.Equal(2003, version.GetProperty("createdAt").GetDateTimeOffset().Year);

        // The SA (no CanImport) is refused a PAST filing date…
        var backdate = await sa.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt", filedAt = "2003-05-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.Forbidden, backdate.StatusCode);

        // …but an omitted filing date defaults to now (current-year bucket), which any editor may do.
        var normal = await TestJson.Post(sa, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        Assert.Contains($"/{tenantId}/{DateTimeOffset.UtcNow.Year}/", normal.GetProperty("uploadUrl").GetString()!);
    }

    // PUT /origin needs the document's current ETag as If-Match — fetch it, then send.
    private static async Task<HttpResponseMessage> SetOriginAsync(HttpClient client, Guid docId, Guid originTenantId, Guid originDocumentId)
    {
        var etag = (await client.GetAsync($"/api/documents/{docId}")).Headers.ETag?.Tag;
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/origin")
        {
            Content = JsonContent.Create(new { originTenantId, originDocumentId }),
        };
        if (etag is not null)
        {
            req.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        return await client.SendAsync(req);
    }
}
