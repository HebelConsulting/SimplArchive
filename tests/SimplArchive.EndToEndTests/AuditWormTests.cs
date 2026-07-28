using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres + object-lock MinIO, exercising audit-log WORM (ADR "Audit-log WORM"):
// after some auditable actions, the archiver seals the events into an NDJSON segment that is written to the
// Object-Lock bucket and retention-LOCKED (immutable), and the worm-segments endpoint lists it.
[Collection(E2ECollection.Name)]
public class AuditWormTests
{
    private readonly E2EApiFactory _factory;

    public AuditWormTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Seals_audit_events_into_a_retention_locked_worm_segment()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A tenant-admin user with CanViewAuditLog. Logging in audits Auth.LoggedIn; deleting a document audits
        // Document.Deleted — a couple of events to seal.
        var email = $"worm-audit-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "worm-1234", "Auditor", canViewAuditLog: true);
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "worm-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Audit {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "audit-doc" })).GetProperty("id").GetGuid();
        await SendDeleteAsync(admin, docId); // audits Document.Deleted

        // Run the WORM archiver (the hosted worker's on-demand equivalent).
        int sealed_;
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            sealed_ = await scope.ServiceProvider.GetRequiredService<IAuditWormArchiver>().ArchiveAsync(tenantId);
        }
        Assert.True(sealed_ >= 1, "expected at least one audit event to be sealed");

        // A segment object exists under the tenant's audit-worm prefix and is retention-locked (immutable).
        using (var scope = _factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
            var objects = await storage.ListObjectsAsync($"tenants/{tenantId}/audit-worm/");
            var segment = Assert.Single(objects);
            Assert.EndsWith(".ndjson", segment.Key);

            var lockStatus = await storage.GetLockStatusAsync(segment.Key);
            Assert.NotNull(lockStatus.RetainUntil);
            Assert.True(lockStatus.RetainUntil > DateTimeOffset.UtcNow, "the segment must be retention-locked");

            // The NDJSON content is one line per sealed event carrying the chain fields.
            await using var stream = await storage.GetObjectAsync(segment.Key);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            var lines = content.TrimEnd('\n').Split('\n');
            Assert.All(lines, l => Assert.Contains("\"hash\"", l));
        }

        // The worm-segments endpoint lists the sealed segment for the tenant admin.
        var listed = await TestJson.Get(admin, "/api/audit-events/worm-segments");
        var segments = listed.GetProperty("segments").EnumerateArray().ToList();
        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.True(s.GetProperty("toSequence").GetInt64() >= s.GetProperty("fromSequence").GetInt64()));
        Assert.Contains(segments, s => s.TryGetProperty("lockedUntil", out var lu) && lu.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    private static async Task<System.Net.Http.HttpResponseMessage> SendDeleteAsync(HttpClient client, Guid documentId)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{documentId}");
        var etag = (await client.SendAsync(head)).Headers.ETag!.ToString();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{documentId}");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
}
