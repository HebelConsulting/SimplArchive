using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Notifications;

/// <summary>
/// Drains <see cref="ExternalEmailOutbox"/> (ABI 1.10, ADR 0902) in the shape of the notification mail outbox (ADR 0840):
/// each row is CLAIMED by compare-and-swap before anything is sent (ADR 0836, both instances run this), a claim older than
/// <see cref="ClaimLifetime"/> is reclaimed, a transient failure goes back to Pending, a permanent one or the fifth
/// attempt is abandoned with an Error and an audit event, and a sent row is deleted.
/// </summary>
/// <remarks>
/// Each item is sent in its OWN scope with the ambient tenant set to the row's tenant, so the SMTP account resolved is that
/// tenant's own (or the installation's). The attachment is read through <see cref="IObjectStorageClient"/>, so at-rest
/// encryption is undone by the seam, as for every other read.
/// </remarks>
public sealed class ExternalEmailDispatcher(SimplArchiveDbContext dbContext, IServiceScopeFactory scopes, ILogger<ExternalEmailDispatcher> logger)
{
    public static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(15);
    public const int MaxAttempts = 5;
    public const string SentAction = "Notification.ExternalMailSent";
    public const string AbandonedAction = "Notification.ExternalMailAbandoned";
    private const int BatchSize = 100;

    /// <summary>Sends what is pending; returns how many were sent.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await ReclaimAbandonedClaimsAsync(now, cancellationToken);

        // Ordered in memory: SQLite (the integration tests' provider) cannot order by DateTimeOffset.
        var pending = (await dbContext.ExternalEmailOutbox.AsNoTracking()
                .Where(o => o.State == EmailOutboxState.Pending)
                .ToListAsync(cancellationToken))
            .OrderBy(o => o.EnqueuedAt).ThenBy(o => o.Id).Take(BatchSize).ToList();

        var sent = 0;
        foreach (var item in pending)
        {
            var won = await dbContext.ExternalEmailOutbox
                .Where(o => o.Id == item.Id && o.State == EmailOutboxState.Pending)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.State, EmailOutboxState.Sending).SetProperty(o => o.ClaimedAt, now), cancellationToken);
            if (won == 0)
            {
                continue;   // the other instance took it
            }

            if (await SendOneAsync(item, cancellationToken))
            {
                sent++;
            }
        }

        return sent;
    }

    private async Task<bool> SendOneAsync(ExternalEmailOutbox item, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = item.TenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        try
        {
            byte[]? attachment = null;
            if (item.AttachmentVersionId is { } versionId)
            {
                var key = await db.DocumentVersions.Where(v => v.Id == versionId).Select(v => v.ObjectKey).SingleAsync(cancellationToken);
                await using var stream = await scope.ServiceProvider.GetRequiredService<IObjectStorageClient>().GetObjectAsync(key, cancellationToken);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                attachment = buffer.ToArray();
            }

            await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new EmailMessage(item.RecipientAddress, item.Subject, item.Body)
            {
                ReplyTo = item.ReplyTo,
                AttachmentFileName = item.AttachmentFileName,
                AttachmentContent = attachment,
            }, cancellationToken);

            await dbContext.ExternalEmailOutbox.Where(o => o.Id == item.Id).ExecuteDeleteAsync(cancellationToken);
            await RecordAsync(scope, item, SentAction, $"mailed to {item.RecipientAddress} by module {item.ModuleId}", cancellationToken);
            logger.LogDebug("Mailed module {ModuleId}'s notice to {Recipient}.", item.ModuleId, item.RecipientAddress);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var attempts = item.Attempts + 1;
            if (e is PermanentEmailFailureException || attempts >= MaxAttempts)
            {
                logger.LogError(e, "Gave up mailing module {ModuleId}'s notice to {Recipient} after {Attempts} attempt(s).",
                    item.ModuleId, item.RecipientAddress, attempts);
                await dbContext.ExternalEmailOutbox.Where(o => o.Id == item.Id).ExecuteDeleteAsync(cancellationToken);
                await RecordAsync(scope, item, AbandonedAction, $"not mailed to {item.RecipientAddress} (module {item.ModuleId}): {e.GetType().Name}", cancellationToken);
                return false;
            }

            logger.LogWarning(e, "Mailing module {ModuleId}'s notice to {Recipient} failed (attempt {Attempts}); it will be retried.",
                item.ModuleId, item.RecipientAddress, attempts);
            await dbContext.ExternalEmailOutbox.Where(o => o.Id == item.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.State, EmailOutboxState.Pending)
                    .SetProperty(o => o.ClaimedAt, (DateTimeOffset?)null).SetProperty(o => o.Attempts, attempts), cancellationToken);
            return false;
        }
    }

    /// <summary>A claim older than <see cref="ClaimLifetime"/> belonged to an instance that died mid-send: back to
    /// Pending, the attempt counted. The cutoff is compared in memory (SQLite cannot compare DateTimeOffset).</summary>
    private async Task ReclaimAbandonedClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stale = (await dbContext.ExternalEmailOutbox.AsNoTracking()
                .Where(o => o.State == EmailOutboxState.Sending)
                .Select(o => new { o.Id, o.ClaimedAt, o.Attempts })
                .ToListAsync(cancellationToken))
            .Where(o => o.ClaimedAt is null || o.ClaimedAt < now - ClaimLifetime)
            .ToList();
        foreach (var row in stale)
        {
            await dbContext.ExternalEmailOutbox
                .Where(o => o.Id == row.Id && o.State == EmailOutboxState.Sending)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.State, EmailOutboxState.Pending)
                    .SetProperty(o => o.ClaimedAt, (DateTimeOffset?)null).SetProperty(o => o.Attempts, row.Attempts + 1), cancellationToken);
        }
    }

    private static Task RecordAsync(IServiceScope scope, ExternalEmailOutbox item, string action, string details, CancellationToken cancellationToken) =>
        scope.ServiceProvider.GetRequiredService<IAuditRecorder>().RecordForActorAsync(
            AuditActorType.System, Guid.Empty, $"Module {item.ModuleId}", item.TenantId, action,
            details: details, cancellationToken: cancellationToken);
}

/// <summary>Runs <see cref="ExternalEmailDispatcher"/> on a timer, as <see cref="EmailNotificationWorker"/> runs its
/// dispatcher.</summary>
public sealed class ExternalEmailWorker(IServiceScopeFactory scopeFactory, ILogger<ExternalEmailWorker> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Worker} started (interval {Interval}).", nameof(ExternalEmailWorker), Interval);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<ExternalEmailDispatcher>().DispatchPendingAsync(stoppingToken);
                    if (sent > 0)
                    {
                        logger.LogInformation("Mailed {Count} external notice(s).", sent);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "The external notice mail sweep failed; it runs again in {Interval}.", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
