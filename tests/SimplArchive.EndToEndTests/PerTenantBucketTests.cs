using System.Text;
using Amazon.S3;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage, exercising per-tenant buckets (ADR "Per-tenant
// object-storage bucket"): each tenant's blobs land in its own bucket `{prefix}-{tenantId}`, and one tenant's
// object is absent from the other tenant's bucket — hard storage-layer isolation.
[Collection(E2ECollection.Name)]
public class PerTenantBucketTests
{
    private readonly E2EApiFactory _factory;

    public PerTenantBucketTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Each_tenants_blob_lives_in_its_own_bucket()
    {
        var (clientA, secretA, tenantA) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var (clientB, secretB, tenantB) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var a = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientA, secretA));
        using var b = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientB, secretB));

        var keyA = await UploadAsync(a, "alpha");
        var keyB = await UploadAsync(b, "beta");

        using var s3 = _factory.CreateStorageClient();
        var bucketA = E2EApiFactory.BucketForTenant(tenantA);
        var bucketB = E2EApiFactory.BucketForTenant(tenantB);

        // Each object exists only in its own tenant's bucket.
        Assert.True(await ObjectExistsAsync(s3, bucketA, keyA));
        Assert.True(await ObjectExistsAsync(s3, bucketB, keyB));
        Assert.False(await ObjectExistsAsync(s3, bucketB, keyA));
        Assert.False(await ObjectExistsAsync(s3, bucketA, keyB));
    }

    // Creates a repository + document, uploads content, finalizes, and returns the version's object key.
    private static async Task<string> UploadAsync(HttpClient client, string name)
    {
        var repoId = (await TestJson.Post(client, "/api/repositories", new { name = $"Bucket {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(client, $"/api/documents/{repoId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        var objectKey = created.GetProperty("objectKey").GetString()!;
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(name)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
        return objectKey;
    }

    private static async Task<bool> ObjectExistsAsync(IAmazonS3 s3, string bucket, string key)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, key);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
