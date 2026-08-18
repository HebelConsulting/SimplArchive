using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;

namespace SimplArchive.Infrastructure.Audit;

// Streams a tenant's audit events to its SIEM webhook (ADR "Audit webhook streaming"). Delivers the contiguous
// run of events past the tenant's AuditWebhookDeliveredThrough checkpoint (stopping at the first Sequence gap,
// like the WORM archiver), HMAC-SHA256-signs each with the tenant's decrypted secret, and advances the checkpoint
// after each success — durable, at-least-once, resumable. Scoped.
public sealed class AuditWebhookDispatcher : IAuditWebhookDispatcher
{
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);

    private readonly Persistence.SimplArchiveDbContext _dbContext;
    private readonly IAuditWebhookSender _sender;
    private readonly ITransitEncryptor _transit;
    private readonly ILogger<AuditWebhookDispatcher> _logger;

    public AuditWebhookDispatcher(Persistence.SimplArchiveDbContext dbContext, IAuditWebhookSender sender, ITransitEncryptor transit, ILogger<AuditWebhookDispatcher> logger)
    {
        _dbContext = dbContext;
        _sender = sender;
        _transit = transit;
        _logger = logger;
    }

    // Exponential-capped backoff (ADR "Audit webhook delivery retry/backoff"): after N consecutive failures the
    // next attempt is min(cap, base·2^(N-1)) later, so a down endpoint isn't hammered every sweep but recovers
    // quickly once it's back.
    private static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromMinutes(60);

    public async Task<int> DispatchAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null || string.IsNullOrWhiteSpace(tenant.AuditWebhookUrl) || string.IsNullOrEmpty(tenant.AuditWebhookSecret))
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;

        // Backoff gate — while failing, wait until the scheduled next attempt (compared client-side; SQLite can't
        // translate a DateTimeOffset comparison in SQL).
        if (tenant.AuditWebhookNextAttemptAt is { } nextAttempt && nextAttempt > now)
        {
            return 0;
        }

        var pending = await _dbContext.AuditEvents.IgnoreQueryFilters(TenantFilterOnly)
            .Where(e => e.TenantId == tenantId && e.Sequence > tenant.AuditWebhookDeliveredThrough)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var secret = await _transit.DecryptAsync(tenant.AuditWebhookSecret);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        var delivered = 0;
        var expected = tenant.AuditWebhookDeliveredThrough + 1;
        foreach (var e in pending)
        {
            // Only a gap-free run (a higher Sequence can commit before a lower one under concurrency); the tail
            // is picked up next sweep once the gap fills.
            if (e.Sequence != expected)
            {
                break;
            }

            var body = JsonSerializer.Serialize(ToLine(e), LineJson);
            var signature = Convert.ToHexString(HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

            var result = await _sender.SendAsync(tenant.AuditWebhookUrl, body, signature, cancellationToken);
            if (!result.Success)
            {
                // Record the failure + schedule the next attempt with backoff, then stop (retry this event later).
                tenant.AuditWebhookConsecutiveFailures++;
                tenant.AuditWebhookLastFailureAt = now;
                tenant.AuditWebhookLastError = Truncate(result.Error);
                tenant.AuditWebhookNextAttemptAt = now + Backoff(tenant.AuditWebhookConsecutiveFailures);
                await _dbContext.SaveChangesAsync(cancellationToken);

                // Until now this failure existed ONLY as columns on the tenant row. The audit trail's whole
                // point is to reach somebody else's SIEM, so a broken feed is the one failure that cannot
                // announce itself through the channel it broke — an operator watching logs saw nothing while
                // events silently stopped arriving (#595, ADR 0626).
                //
                // The event is NOT lost: delivery resumes from AuditWebhookDeliveredThrough, so this is a
                // stalled feed rather than a gap in the record. That distinction belongs in the message,
                // because "audit delivery failed" otherwise reads as data loss.
                _logger.LogWarning(
                    "Audit webhook delivery to tenant {TenantId} failed ({Failures} consecutive): {Error}. "
                    + "Event {Sequence} and everything after it is undelivered and will be retried after "
                    + "{NextAttempt:u}; nothing is lost.",
                    tenant.Id, tenant.AuditWebhookConsecutiveFailures, tenant.AuditWebhookLastError,
                    e.Sequence, tenant.AuditWebhookNextAttemptAt);

                // Past the point where the backoff has stretched to its cap, the feed is not "retrying" in any
                // useful sense — it is down, and an hour of audit events is queuing per attempt. That is an
                // admin's problem to act on, not a transient blip.
                if (Backoff(tenant.AuditWebhookConsecutiveFailures) >= BackoffCap)
                {
                    _logger.LogError(
                        "Audit webhook for tenant {TenantId} has failed {Failures} times in a row and is now "
                        + "retrying at the maximum interval ({Cap}). Audit events are NOT reaching the "
                        + "configured SIEM.",
                        tenant.Id, tenant.AuditWebhookConsecutiveFailures, BackoffCap);
                }

                break;
            }

            // Recovery is worth one line: an operator who saw the Warnings needs to know they stopped for a
            // good reason rather than because the sweep gave up.
            if (tenant.AuditWebhookConsecutiveFailures > 0)
            {
                _logger.LogInformation(
                    "Audit webhook delivery to tenant {TenantId} recovered after {Failures} consecutive failures.",
                    tenant.Id, tenant.AuditWebhookConsecutiveFailures);
            }

            tenant.AuditWebhookDeliveredThrough = e.Sequence;
            tenant.AuditWebhookLastSuccessAt = now;
            // A success clears the failure state (resets the backoff).
            tenant.AuditWebhookConsecutiveFailures = 0;
            tenant.AuditWebhookNextAttemptAt = null;
            tenant.AuditWebhookLastError = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            delivered++;
            expected++;
        }

        return delivered;
    }

    private static TimeSpan Backoff(int consecutiveFailures)
    {
        // base · 2^(failures-1), capped. Guard the shift against overflow for a long-dead endpoint.
        var factor = consecutiveFailures >= 30 ? long.MaxValue : 1L << (consecutiveFailures - 1);
        var ticks = BackoffBase.Ticks <= BackoffCap.Ticks / factor ? BackoffBase.Ticks * factor : BackoffCap.Ticks;
        return TimeSpan.FromTicks(Math.Min(ticks, BackoffCap.Ticks));
    }

    private static string? Truncate(string? error) =>
        error is { Length: > 500 } ? error[..500] : error;

    // Mirrors the NDJSON export line (ADR "Audit trail export") — Sequence + Hash let the SIEM verify the chain.
    private static object ToLine(AuditEvent e) => new
    {
        sequence = e.Sequence,
        hash = e.Hash,
        timestamp = e.Timestamp,
        actorType = e.ActorType.ToString(),
        actorId = e.ActorId,
        actorName = e.ActorName,
        action = e.Action,
        targetType = e.TargetType,
        targetId = e.TargetId,
        targetName = e.TargetName,
        details = e.Details,
    };
}
