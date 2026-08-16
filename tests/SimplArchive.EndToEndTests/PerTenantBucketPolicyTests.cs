using Amazon.S3.Model;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object storage, exercising per-tenant bucket policy knobs (ADR
// "Per-tenant bucket policy knobs"): a tenant's bucket carries the ops tags + the abort-incomplete-multipart
// lifecycle rule at creation, and updating the setting via api/tenant-settings re-applies the lifecycle config.
// Verifies the config round-trips on the real bucket (SeaweedFS); the lifecycle *effect* runs only on a
// lifecycle-capable backend (e.g. AWS S3).
[Collection(E2ECollection.Name)]
public class PerTenantBucketPolicyTests
{
    private readonly E2EApiFactory _factory;

    public PerTenantBucketPolicyTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Bucket_carries_tags_and_lifecycle_that_the_tenant_setting_updates()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var adminEmail = $"bp-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "bp-1234", "Bucket Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "bp-1234"));

        using var s3 = _factory.CreateStorageClient();
        var bucket = E2EApiFactory.BucketForTenant(tenantId);

        // The seed helper (like provisioning) tags the bucket with the tenant id + application marker.
        var tags = await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket });
        Assert.Contains(tags.TagSet, t => t.Key == "tenant-id" && t.Value == tenantId.ToString("D"));
        Assert.Contains(tags.TagSet, t => t.Key == "application" && t.Value == "simplarchive");

        // Set the lifecycle setting, then confirm the bucket carries the abort-incomplete-multipart rule.
        var name = (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("name").GetString();
        await TestJson.Put(admin, "/api/tenant-settings/storage", new { storageQuotaBytes = (long?)null, incompleteUploadCleanupDays = 9 });
        Assert.Equal(9, (await TestJson.Get(admin, "/api/tenant-settings")).GetProperty("incompleteUploadCleanupDays").GetInt32());

        var lifecycle = await s3.GetLifecycleConfigurationAsync(bucket);
        var rule = Assert.Single(lifecycle.Configuration.Rules);
        Assert.Equal(9, rule.AbortIncompleteMultipartUpload.DaysAfterInitiation);

        // Setting it to 0 removes the lifecycle configuration (a missing config may 404, depending on the backend).
        await TestJson.Put(admin, "/api/tenant-settings/storage", new { storageQuotaBytes = (long?)null, incompleteUploadCleanupDays = 0 });
        try
        {
            var cleared = await s3.GetLifecycleConfigurationAsync(bucket);
            Assert.True(cleared.Configuration?.Rules is null or { Count: 0 });
        }
        catch (Amazon.S3.AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // No lifecycle configuration — the expected cleared state.
        }
    }
}
