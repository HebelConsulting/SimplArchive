using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + MinIO, exercising permanent purge (ADR "Manual hard-delete /
// purge"): a tenant admin purges a recycle-bin document, removing its rows and object-storage blobs; purging an
// active document is refused, and a non-admin can't purge.
[Collection(E2ECollection.Name)]
public class PurgeTests
{
    private readonly E2EApiFactory _factory;

    public PurgeTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tenant_admin_purges_a_recycle_bin_document_and_its_blob()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "purge-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // A document with a confirmed version (so there's a stored blob to purge). The owner grants the admin
        // is unnecessary — a tenant admin sees everything via the ACL bypass.
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Purge {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "doomed" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var objectKey = created.GetProperty("objectKey").GetString()!;
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("bytes")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        var stem = objectKey[..^Path.GetExtension(objectKey).Length];
        Assert.NotEmpty(await _factory.ListObjectKeysAsync(stem)); // the blob exists

        // Soft-delete, then purge as the tenant admin.
        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{docId}");
        deleteReq.Headers.TryAddWithoutValidation("If-Match", (await admin.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{docId}"))).Headers.ETag!.ToString());
        (await admin.SendAsync(deleteReq)).EnsureSuccessStatusCode();

        (await admin.PostAsync($"/api/documents/{docId}/purge", null)).EnsureSuccessStatusCode();

        // The document is gone (not even in the recycle bin) and its blob is deleted.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{docId}")).StatusCode);
        Assert.Empty(await _factory.ListObjectKeysAsync(stem));
    }

    [Fact]
    public async Task Purge_is_refused_for_an_active_document_and_a_non_admin()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        var plainEmail = $"plain-{Guid.NewGuid():N}@e2e.local";
        const string password = "purge-1234";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        await _factory.SeedUserAsync(tenantId, plainEmail, password, "Plain");
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Purge {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "active" })).GetProperty("id").GetGuid();

        // An active (not-recycled) document can't be purged.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/documents/{docId}/purge", null)).StatusCode);

        // A non-admin can't purge even a recycle-bin item.
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"/api/documents/{docId}/purge", null)).StatusCode);
    }
}
