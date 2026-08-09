using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Notifications;

// Emails the not-yet-emailed in-app notifications (ADR "Email notifications (SMTP)"). Drives email off the
// existing Notification rows: scans EmailedAt == null across all tenants, resolves the recipient's User
// email/name, sends a plain-text message, and stamps EmailedAt on success. A failed send leaves EmailedAt null
// (and is not stamped), so the next sweep retries it — at-least-once, like the search outbox. Each message is
// committed independently so one bad send can't roll back the rest.
public sealed class EmailNotificationDispatcher : IEmailNotificationDispatcher
{
    // A bounded batch per pass keeps one sweep's work small; the rest are picked up next tick.
    private const int BatchSize = 200;
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<EmailNotificationDispatcher> _logger;

    public EmailNotificationDispatcher(SimplArchiveDbContext dbContext, IEmailSender emailSender, ILogger<EmailNotificationDispatcher> logger)
    {
        _dbContext = dbContext;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        // Un-emailed notifications joined to their recipient (a Restrict FK, so the user always exists). The
        // tenant filter is ignored (this sweep spans all tenants).
        //
        // Ordered by Id: arbitrary, but STABLE, which is what a batched read needs. Not chronological — SQLite
        // (the test provider) refuses DateTimeOffset in ORDER BY outright (NotSupportedException), so CreatedAt
        // is not an option here. Order does not affect which notifications get sent: a sent one has EmailedAt
        // stamped and leaves this set, so successive sweeps drain it. The exception is an address that fails
        // PERMANENTLY — it stays pending forever, and BatchSize of those would stall everything behind them.
        // That needs an attempt counter or a dead-letter row, not an ORDER BY; tracked as issue #433.
        var pending = await (
            from n in _dbContext.Notifications.IgnoreQueryFilters(TenantFilterOnly)
            where n.EmailedAt == null
            join u in _dbContext.Users.IgnoreQueryFilters(TenantFilterOnly) on n.RecipientUserId equals u.Id
            orderby n.Id
            select new { Notification = n, u.Email, u.DisplayName })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        // Email-channel preferences (ADR "Notification preferences"): a (user, type) with EmailEnabled = false is
        // suppressed. Only mutable types ever have a row, so the escalation types are never muted. Absence = on.
        var recipientIds = pending.Select(p => p.Notification.RecipientUserId).Distinct().ToList();
        var muted = (await _dbContext.UserNotificationPreferences.IgnoreQueryFilters(TenantFilterOnly)
                .Where(p => recipientIds.Contains(p.UserId) && !p.EmailEnabled)
                .Select(p => new { p.UserId, p.Type })
                .ToListAsync(cancellationToken))
            .Select(p => (p.UserId, p.Type))
            .ToHashSet();

        var sent = 0;
        foreach (var item in pending)
        {
            // Suppressed by the recipient's preference: mark handled (so it isn't re-scanned) without sending.
            if (muted.Contains((item.Notification.RecipientUserId, item.Notification.Type)))
            {
                item.Notification.EmailedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            try
            {
                await _emailSender.SendAsync(item.Email, item.DisplayName, item.Notification.Title, item.Notification.Body, cancellationToken);
                item.Notification.EmailedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
                sent++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // Leave EmailedAt null so the next sweep retries; don't let one bad address stall the batch.
                _logger.LogWarning(e, "Failed to email notification {NotificationId}; will retry next sweep.", item.Notification.Id);
            }
        }

        return sent;
    }
}
