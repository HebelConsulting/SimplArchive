using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + MinIO, exercising the stale-check-out auto-release sweep (ADR
// "Stale check-out auto-release sweep"): a check-out (with a cloud working-copy stash) whose CheckedOutAt is
// older than the tenant's CheckoutTtlDays is released by IStaleCheckoutService.SweepAsync — the lock clears,
// the MinIO stash blob is deleted, and the former holder gets a CheckoutExpired notification.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StaleCheckoutTests
{
    private readonly E2EApiFactory _factory;

    public StaleCheckoutTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Sweep_releases_a_stale_checkout_deletes_its_stash_and_notifies_the_holder()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"SC {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "stale-lock-doc" })).GetProperty("id").GetGuid();
        await UploadConfirmedVersionAsync(owner, docId, "v1 content");

        // A tenant-admin user takes the lock (ACL bypass → CanEditContent) and stashes a working copy in the cloud.
        var holderEmail = $"sc-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, holderEmail, "sc-1234", "Holder");
        await _factory.GrantTenantAdminAsync(holderEmail);
        using var holder = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(holderEmail, "sc-1234"));

        var holderId = (await TestJson.Get(holder, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid();
        (await holder.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();

        var stashUrl = (await TestJson.Post(holder, $"/api/checkouts/{docId}/working-copy", new { })).GetProperty("uploadUrl").GetString()!;
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(stashUrl, new ByteArrayContent(Encoding.UTF8.GetBytes("in-progress edits")))).EnsureSuccessStatusCode();
        }

        var stashKey = CheckoutStashKey.Build(tenantId, holderId, docId);

        // Enable the sweep for this tenant (TTL 1 day) and backdate the lock to 30 days ago so it's stale.
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var storageClient = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
            Assert.True(await storageClient.ExistsAsync(stashKey)); // the stash blob is there before the sweep

            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            tenant.CheckoutTtlDays = 1;
            var doc = await db.Documents.SingleAsync(d => d.Id == docId);
            doc.CheckedOutAt = DateTimeOffset.UtcNow.AddDays(-30);
            await db.SaveChangesAsync();
        }

        // Run the sweep.
        using (var scope = _factory.Services.CreateScope())
        {
            var released = await scope.ServiceProvider.GetRequiredService<IStaleCheckoutService>().SweepAsync();
            Assert.True(released >= 1);
        }

        // The lock is gone (the document no longer reports a check-out; the holder's list is empty).
        Assert.Null(GetCheckedOut(await TestJson.Get(owner, $"/api/documents/{docId}")));
        Assert.Empty((await TestJson.Get(holder, "/api/checkouts")).GetProperty("items").EnumerateArray());

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var storageClient = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

            // The stash blob was deleted.
            Assert.False(await storageClient.ExistsAsync(stashKey));

            // The former holder was notified.
            var notified = await db.Notifications.AnyAsync(n =>
                n.RecipientUserId == holderId && n.Type == NotificationType.CheckoutExpired && n.DocumentId == docId);
            Assert.True(notified);
        }
    }

    private static async Task UploadConfirmedVersionAsync(HttpClient client, Guid docId, string content)
    {
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using var storage = new HttpClient();
        (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
    }

    private static System.Text.Json.JsonElement? GetCheckedOut(System.Text.Json.JsonElement document) =>
        document.TryGetProperty("checkedOut", out var c) && c.ValueKind != System.Text.Json.JsonValueKind.Null ? c : null;
}
