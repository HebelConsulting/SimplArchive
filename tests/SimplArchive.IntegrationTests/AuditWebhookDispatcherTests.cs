using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Audit;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Secrets;

namespace SimplArchive.IntegrationTests;

// AuditWebhookDispatcher (ADR "Audit webhook streaming"): streams a tenant's audit events past its delivery
// checkpoint to the configured webhook, HMAC-SHA256-signing each; advances the checkpoint per success and stops
// on a failure so the same event retries next sweep. Uses a recording sender (no real HTTP) + the null transit
// encryptor (the stored secret is plaintext here).
public class AuditWebhookDispatcherTests
{
    private const string Secret = "sup3r-s3cret";

    private sealed class RecordingSender : IAuditWebhookSender
    {
        public List<(string Body, string Signature)> Sent { get; } = [];
        public Func<int, bool> Succeeds { get; set; } = _ => true;
        public Task<WebhookSendResult> SendAsync(string url, string jsonBody, string signature, CancellationToken cancellationToken = default)
        {
            if (!Succeeds(Sent.Count))
            {
                return Task.FromResult(WebhookSendResult.Fail("HTTP 503"));
            }

            Sent.Add((jsonBody, signature));
            return Task.FromResult(WebhookSendResult.Ok);
        }
    }

    private readonly Guid _tenantId = Guid.NewGuid();
    private SimplArchiveDbContext Ctx(SqliteConnection c) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task SeedAsync(SqliteConnection connection, int eventCount)
    {
        using var db = Ctx(connection);
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow, AuditWebhookUrl = "https://siem.example/ingest", AuditWebhookSecret = Secret });
        for (var i = 0; i < eventCount; i++)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                Timestamp = DateTimeOffset.UtcNow,
                ActorType = AuditActorType.User,
                ActorId = Guid.NewGuid(),
                ActorName = $"Actor {i}",
                Action = "Test.Event",
                Sequence = i,
                Hash = new string((char)('a' + i), 64),
            });
        }

        await db.SaveChangesAsync();
    }

    private AuditWebhookDispatcher Dispatcher(SimplArchiveDbContext db, RecordingSender sender) =>
        new(db, sender, new NullTransitEncryptor(), NullLogger<AuditWebhookDispatcher>.Instance);

    [Fact]
    public async Task Delivers_all_events_signed_and_advances_the_checkpoint()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedAsync(connection, 3);

        var sender = new RecordingSender();
        int delivered;
        using (var db = Ctx(connection))
        {
            delivered = await Dispatcher(db, sender).DispatchAsync(_tenantId, CancellationToken.None);
        }

        Assert.Equal(3, delivered);
        Assert.Equal(3, sender.Sent.Count);

        // Each delivery is HMAC-SHA256(secret, body) in lowercase hex.
        foreach (var (body, signature) in sender.Sent)
        {
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            Assert.Equal(expected, signature);
        }

        // The checkpoint advanced to the last delivered Sequence; a second dispatch is a no-op.
        using (var db = Ctx(connection))
        {
            Assert.Equal(2, (await db.Tenants.SingleAsync()).AuditWebhookDeliveredThrough);
            Assert.Equal(0, await Dispatcher(db, new RecordingSender()).DispatchAsync(_tenantId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Stops_on_a_failed_delivery_records_health_and_resumes_after_backoff()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedAsync(connection, 3);

        // Fail the 2nd send: only the first event is delivered, checkpoint = 0, and the failure is recorded with a
        // scheduled next attempt (backoff) so a broken endpoint isn't hammered.
        using (var db = Ctx(connection))
        {
            var flaky = new RecordingSender { Succeeds = i => i != 1 };
            Assert.Equal(1, await Dispatcher(db, flaky).DispatchAsync(_tenantId, CancellationToken.None));
            var t = await db.Tenants.SingleAsync();
            Assert.Equal(0, t.AuditWebhookDeliveredThrough);
            Assert.Equal(1, t.AuditWebhookConsecutiveFailures);
            Assert.Equal("HTTP 503", t.AuditWebhookLastError);
            Assert.NotNull(t.AuditWebhookLastFailureAt);
            Assert.NotNull(t.AuditWebhookNextAttemptAt);
            Assert.True(t.AuditWebhookNextAttemptAt > DateTimeOffset.UtcNow); // scheduled into the future
        }

        // While backing off (next attempt in the future), a dispatch is skipped even though the endpoint is healthy.
        using (var db = Ctx(connection))
        {
            Assert.Equal(0, await Dispatcher(db, new RecordingSender()).DispatchAsync(_tenantId, CancellationToken.None));
        }

        // Simulate the backoff window elapsing; the next dispatch delivers the remaining two and resets the health.
        using (var db = Ctx(connection))
        {
            var t = await db.Tenants.SingleAsync();
            t.AuditWebhookNextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection))
        {
            Assert.Equal(2, await Dispatcher(db, new RecordingSender()).DispatchAsync(_tenantId, CancellationToken.None));
            var t = await db.Tenants.SingleAsync();
            Assert.Equal(2, t.AuditWebhookDeliveredThrough);
            Assert.Equal(0, t.AuditWebhookConsecutiveFailures);
            Assert.Null(t.AuditWebhookNextAttemptAt);
            Assert.Null(t.AuditWebhookLastError);
            Assert.NotNull(t.AuditWebhookLastSuccessAt);
        }
    }
}
