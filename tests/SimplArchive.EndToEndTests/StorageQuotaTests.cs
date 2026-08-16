using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage, exercising the per-tenant storage quota (ADR
// "Per-tenant storage quota"): a tenant admin sets a small quota; an upload within it succeeds and raises the
// tenant's used-storage counter; an upload that would exceed it is refused (409 STORAGE_QUOTA_EXCEEDED); purging
// the first document frees the storage back.
[Collection(E2ECollection.Name)]
public class StorageQuotaTests
{
    private readonly E2EApiFactory _factory;

    public StorageQuotaTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Quota_is_enforced_at_finalize_accounted_and_freed_on_purge()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var adminEmail = $"sq-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "sq-1234", "Quota Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "sq-1234"));

        // Set a 1000-byte quota (keep the tenant's existing name).
        var name = (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("name").GetString();
        await TestJson.Put(admin, "/api/tenant-settings/storage", new { storageQuotaBytes = 1000, incompleteUploadCleanupDays = 0 });

        // The service account (CanManageRepositories) creates the repository; the tenant admin (IsTenantAdmin ACL
        // bypass) then uploads into it and purges — IsTenantAdmin doesn't imply CanManageRepositories.
        using var sa = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(sa, "/api/repositories", new { name = $"Quota {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // A 400-byte upload fits → confirmed; the counter reflects it.
        var (firstDoc, firstFinalize) = await UploadAsync(admin, repoId, "within", new string('a', 400));
        Assert.Equal(HttpStatusCode.OK, firstFinalize.StatusCode);
        Assert.Equal(400, (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("storageUsedBytes").GetInt64());

        // An 800-byte upload would push usage to 1200 > 1000 → refused at finalize, and the counter is unchanged.
        var (_, overFinalize) = await UploadAsync(admin, repoId, "over", new string('b', 800));
        Assert.Equal(HttpStatusCode.Conflict, overFinalize.StatusCode);
        Assert.Equal("STORAGE_QUOTA_EXCEEDED", (await overFinalize.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal(400, (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("storageUsedBytes").GetInt64());

        // Delete + purge the first document → its 400 bytes are freed from the counter.
        var etag = (await admin.GetAsync($"/api/documents/{firstDoc}")).Headers.ETag!.Tag;
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{firstDoc}");
        delete.Headers.TryAddWithoutValidation("If-Match", etag);
        (await admin.SendAsync(delete)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/documents/{firstDoc}/purge", null)).EnsureSuccessStatusCode();

        Assert.Equal(0, (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("storageUsedBytes").GetInt64());
    }

    // Creates a document under the folder, uploads the given content, and returns the document id + the finalize
    // response (so the caller can assert success vs a quota rejection).
    private static async Task<(Guid DocumentId, HttpResponseMessage Finalize)> UploadAsync(HttpClient client, Guid folderId, string docName, string content)
    {
        var docId = (await TestJson.Post(client, $"/api/documents/{folderId}/children", new { name = docName })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        var finalize = await client.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { });
        return (docId, finalize);
    }
}
