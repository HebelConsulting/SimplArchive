using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object-lock-enabled MinIO, exercising WORM immutability (ADR "WORM
// / immutable document versions (S3 Object Lock)"): placing a legal hold applies an S3 object legal hold to the
// document's version blob (and releasing lifts it), and a retention policy applies an Object Lock retention that
// refuses a purge (409 WORM_LOCKED) until it expires.
[Collection(E2ECollection.Name)]
public class WormObjectLockTests
{
    private readonly E2EApiFactory _factory;

    public WormObjectLockTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Legal_hold_applies_and_lifts_an_object_legal_hold_on_the_blob()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"worm-lh-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "worm-1234", "Compliance");
        await _factory.GrantCanLegalHoldAsync(email);
        using var compliance = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "worm-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"WORM {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "worm-lh-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "held content");

        var objectKey = await GetConfirmedObjectKeyAsync(tenantId, docId);

        // Before the hold: no legal hold on the blob.
        Assert.False((await GetLockStatusAsync(objectKey)).LegalHold);

        // Place a legal hold → the blob's object legal hold is on.
        var holdId = (await TestJson.Post(compliance, "/api/legal-holds", new { name = "Matter WORM", reason = "litigation" })).GetProperty("id").GetGuid();
        (await compliance.PostAsJsonAsync($"/api/legal-holds/{holdId}/items", new { documentId = docId })).EnsureSuccessStatusCode();
        Assert.True((await GetLockStatusAsync(objectKey)).LegalHold);

        // Release the hold → the object legal hold is lifted.
        (await compliance.PostAsync($"/api/legal-holds/{holdId}/release", null)).EnsureSuccessStatusCode();
        Assert.False((await GetLockStatusAsync(objectKey)).LegalHold);
    }

    [Fact]
    public async Task Retention_locks_the_blob_and_purge_is_refused_until_it_expires()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A tenant admin to run the purge (purge is tenant-admin-only; a ServiceAccount can't).
        var adminEmail = $"worm-adm-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "worm-1234", "Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "worm-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"WORM {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "worm-ret-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "retained content");

        // Seed a mask with a 7-year retention and assign it → the reconcile applies an Object Lock retention.
        var maskId = await SeedRetentionMaskAsync(tenantId, years: 7);
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();

        var objectKey = await GetConfirmedObjectKeyAsync(tenantId, docId);
        var status = await GetLockStatusAsync(objectKey);
        Assert.NotNull(status.RetainUntil);
        Assert.True(status.RetainUntil > DateTimeOffset.UtcNow.AddYears(6), "expected a ~7-year retention lock");

        // Soft-delete the document (retention doesn't block a manual delete — only legal hold does).
        Assert.True((await SendDeleteAsync(owner, docId)).IsSuccessStatusCode);

        // The tenant admin's purge is refused: the blob is still under WORM retention.
        var purge = await admin.PostAsync($"/api/documents/{docId}/purge", null);
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        Assert.Contains("WORM_LOCKED", await purge.Content.ReadAsStringAsync());
    }

    private async Task<string> GetConfirmedObjectKeyAsync(Guid tenantId, Guid docId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        return await db.DocumentVersions
            .Where(v => v.DocumentId == docId && v.Status == DocumentVersionStatus.Confirmed)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => v.ObjectKey!)
            .FirstAsync();
    }

    private async Task<ObjectLockStatus> GetLockStatusAsync(string objectKey)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IObjectStorageClient>().GetLockStatusAsync(objectKey);
    }

    private async Task<Guid> SeedRetentionMaskAsync(Guid tenantId, int years)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
        var version = new MaskVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskId = mask.Id,
            Name = $"Retained {years}y {Guid.NewGuid():N}",
            RetentionYears = years,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Masks.Add(mask);
        db.MaskVersions.Add(version);
        await db.SaveChangesAsync();
        return mask.Id;
    }

    private static async Task UploadConfirmedVersionAsync(HttpClient client, Guid docId, string content)
    {
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using var storage = new HttpClient();
        (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
    }

    private static async Task<HttpResponseMessage> SendDeleteAsync(HttpClient client, Guid documentId)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{documentId}");
        var etag = (await client.SendAsync(head)).Headers.ETag!.ToString();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{documentId}");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
}
