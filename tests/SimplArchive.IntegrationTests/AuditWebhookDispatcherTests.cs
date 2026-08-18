using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    private AuditWebhookDispatcher Dispatcher(SimplArchiveDbContext db, RecordingSender sender, CapturingLogger log) =>
        new(db, sender, new NullTransitEncryptor(), log);

    /// <summary>Records what the dispatcher logged, so the signal itself can be asserted.</summary>
    /// <remarks>
    /// A broken audit feed is the one failure that cannot announce itself through the channel it broke — the
    /// events are meant to reach somebody else's SIEM, and when they stop, the SIEM is the last to know. So
    /// the log line IS the behaviour here, not a side effect of it, and asserting it is the only way to pin
    /// that the failure is announced at all (#595, ADR 0626).
    /// </remarks>
    private sealed class CapturingLogger : ILogger<AuditWebhookDispatcher>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

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

    [Fact]
    public async Task A_failing_feed_is_announced_and_its_recovery_too()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedAsync(connection, 2);

        var sender = new RecordingSender { Succeeds = _ => false };
        var log = new CapturingLogger();

        using (var db = Ctx(connection))
        {
            await Dispatcher(db, sender, log).DispatchAsync(_tenantId, CancellationToken.None);
        }

        // The failure must be visible in the LOG, not only as columns on the tenant row — that was the gap.
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(_tenantId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 503", warning.Message, StringComparison.Ordinal);

        // And it must say the events are NOT lost. "Audit delivery failed" otherwise reads as a hole in the
        // record, which would send an operator hunting for something that never happened.
        Assert.Contains("nothing is lost", warning.Message, StringComparison.OrdinalIgnoreCase);

        // Now let it through: recovery is its own line, so an operator who saw the warnings knows they stopped
        // for a good reason rather than because the sweep gave up.
        sender.Succeeds = _ => true;

        // Make the tenant due again. The failure above scheduled the next attempt a backoff into the future,
        // and the dispatcher honours that — so without this the recovery sweep skips the tenant entirely and
        // logs nothing, which is correct behaviour and not what this test is about.
        using (var db = Ctx(connection))
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == _tenantId);
            tenant.AuditWebhookNextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var recovery = new CapturingLogger();
        using (var db = Ctx(connection))
        {
            await Dispatcher(db, sender, recovery).DispatchAsync(_tenantId, CancellationToken.None);
        }

        var recovered = Assert.Single(recovery.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("recovered", recovered.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_feed_that_has_stopped_retrying_usefully_is_raised_to_Error()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        await SeedAsync(connection, 1);

        // Put the tenant just below the point where backoff reaches its cap, so ONE more failure crosses it.
        using (var db = Ctx(connection))
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == _tenantId);
            tenant.AuditWebhookConsecutiveFailures = 20;
            await db.SaveChangesAsync();
        }

        var log = new CapturingLogger();
        using (var db = Ctx(connection))
        {
            await Dispatcher(db, new RecordingSender { Succeeds = _ => false }, log).DispatchAsync(_tenantId, CancellationToken.None);
        }

        // At the cap the feed is not "retrying" in any useful sense — it is down, and an hour of audit events
        // queues per attempt. That is an admin's problem to act on, so it escalates past Warning.
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("NOT reaching", error.Message, StringComparison.Ordinal);
    }
}
