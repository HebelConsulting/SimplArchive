using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage: the recompute-storage admin action (ADR "Per-tenant
// storage quota") rebuilds the used-storage counter from the actual confirmed version blobs — fixing a tenant
// whose blobs predate the quota feature (no SizeBytes) or whose counter drifted.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StorageQuotaRecomputeTests
{
    private readonly E2EApiFactory _factory;

    public StorageQuotaRecomputeTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Recompute_rebuilds_used_storage_from_the_actual_blobs()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var adminEmail = $"rc-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "rc-1234", "Recompute Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "rc-1234"));
        using var sa = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(sa, "/api/repositories", new { name = $"Recompute {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        await UploadAsync(sa, repoId, "one", new string('a', 300));
        await UploadAsync(sa, repoId, "two", new string('b', 500));

        // Normal accounting counted both uploads.
        Assert.Equal(800, (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("storageUsedBytes").GetInt64());

        // Simulate a pre-quota tenant: counter zeroed, per-version SizeBytes cleared.
        await _factory.SimulatePreQuotaStateAsync(tenantId);
        Assert.Equal(0, (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("storageUsedBytes").GetInt64());

        // Recompute rebuilds the counter from the actual stored blobs.
        var recomputed = await TestJson.Post(admin, "/api/tenant-settings/recompute-storage", new { });
        Assert.Equal(800, recomputed.GetProperty("storageUsedBytes").GetInt64());
        Assert.Equal(800, (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("storageUsedBytes").GetInt64());

        // A non-admin can't recompute.
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await sa.PostAsync("/api/tenant-settings/recompute-storage", null)).StatusCode);
    }

    private static async Task UploadAsync(HttpClient client, Guid folderId, string name, string content)
    {
        var docId = (await TestJson.Post(client, $"/api/documents/{folderId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
    }
}
