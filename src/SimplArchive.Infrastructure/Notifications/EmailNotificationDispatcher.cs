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

    // How many failures a notification gets before the system stops trying (ADR 0612). Five sweeps is minutes of
    // transient outage absorbed, while a genuinely dead address costs five sends rather than an unbounded number.
    private const int MaxEmailAttempts = 5;

    // The stable audit action code (mirrors Api.Controllers.AuditActions, which this layer can't reference —
    // the same arrangement RetentionService uses for its disposal event).
    private const string EmailAbandonedAction = "Notification.EmailAbandoned";
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEmailSender _emailSender;
    private readonly Encryption.MessageEnvelopeClient _envelopeClient;
    private readonly ILogger<EmailNotificationDispatcher> _logger;
    private readonly IAuditRecorder _audit;

    public EmailNotificationDispatcher(SimplArchiveDbContext dbContext, IEmailSender emailSender,
        Encryption.MessageEnvelopeClient envelopeClient, ILogger<EmailNotificationDispatcher> logger, IAuditRecorder audit)
    {
        _envelopeClient = envelopeClient;
        _dbContext = dbContext;
        _emailSender = emailSender;
        _logger = logger;
        _audit = audit;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        // Un-emailed notifications joined to their recipient (a Restrict FK, so the user always exists). The
        // tenant filter is ignored (this sweep spans all tenants).
        //
        // Ordered by Id: arbitrary, but STABLE, which is what a batched read needs. Not chronological — SQLite
        // (the test provider) refuses DateTimeOffset in ORDER BY outright (NotSupportedException), so CreatedAt
        // is not an option here. Order does not affect which notifications get sent: a sent one has EmailedAt
        // stamped and leaves this set, so successive sweeps drain it.
        //
        // EmailFailedAt is what keeps that true for an address that can NEVER succeed (ADR 0612, issue #433).
        // Before it, such a row stayed pending forever; BatchSize of them made the batch entirely hopeless, and
        // every legitimate notification behind them was never looked at again — a stall that is invisible,
        // because the symptom is mail that does not arrive.
        var pending = await (
            from n in _dbContext.Notifications.IgnoreQueryFilters(TenantFilterOnly)
            where n.EmailedAt == null && n.EmailFailedAt == null
            join u in _dbContext.Users.IgnoreQueryFilters(TenantFilterOnly) on n.RecipientUserId equals u.Id
            join t in _dbContext.Tenants.IgnoreQueryFilters(TenantFilterOnly) on n.TenantId equals t.Id
            orderby n.Id
            select new { Notification = n, u.Email, u.DisplayName, u.SmimeCertificatePem, TenantName = t.Name })
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
                // S/MIME enveloping (#1334): the recipient's own certificate wins (the #1332 column), the
                // sidecar's registry answers for gated tenants (ADR 0813) — the same precedence as the
                // IMAP funnel, fetched as a PUBLIC certificate so ONE in-process enveloping path serves
                // both sources. With a certificate the subject goes GENERIC (headers cannot encrypt) and
                // the title moves into the encrypted body; without one, exactly yesterday's plaintext.
                var certificatePem = item.SmimeCertificatePem
                    ?? await _envelopeClient.TryGetCertificatePemAsync(item.TenantName, item.Email, cancellationToken);
                var (subject, body) = certificatePem is null
                    ? (item.Notification.Title, item.Notification.Body)
                    : ("SimplArchive — new notification", $"{item.Notification.Title}\n\n{item.Notification.Body}");
                await _emailSender.SendAsync(item.Email, item.DisplayName, subject, body, certificatePem, cancellationToken);
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
                item.Notification.EmailAttempts++;

                // A permanent rejection cannot succeed on retry — the mailbox does not exist, the domain does
                // not resolve — so spending the rest of the budget on it is four more useless sends and four
                // more log lines. A transient failure (server down, timeout, mailbox full) uses the budget.
                var permanent = e is PermanentEmailFailureException;
                var exhausted = item.Notification.EmailAttempts >= MaxEmailAttempts;

                if (permanent || exhausted)
                {
                    item.Notification.EmailFailedAt = DateTimeOffset.UtcNow;

                    // Error, not Warning: the retries are routine, giving up is the thing an administrator has
                    // to act on — a wrong address to correct, or a mail server that has been down for hours.
                    _logger.LogError(e,
                        "Gave up emailing notification {NotificationId} to {Recipient} after {Attempts} attempt(s) ({Reason}); the in-app notification is unaffected.",
                        item.Notification.Id, item.Email, item.Notification.EmailAttempts,
                        permanent ? "permanently rejected" : "retry budget exhausted");

                    // …and in the product, not only in a log nobody may be reading. The ACTOR is passed
                    // explicitly too (#1312): this sweep has no ambient principal any more than it has an
                    // ambient tenant, and RecordAsync drops the event when it cannot resolve one — which
                    // is exactly what it silently did here for as long as this call existed. The System
                    // actor with a descriptive subsystem name is the RetentionService idiom.
                    await _audit.RecordForActorAsync(
                        SimplArchive.Domain.Audit.AuditActorType.System, Guid.Empty, "Notification email",
                        item.Notification.TenantId, EmailAbandonedAction, "Notification", item.Notification.Id,
                        item.Notification.Title,
                        $"{(permanent ? "Permanently rejected" : "Retry budget exhausted")} after {item.Notification.EmailAttempts} attempt(s): {e.Message}",
                        cancellationToken);
                }
                else
                {
                    _logger.LogWarning(e, "Failed to email notification {NotificationId} (attempt {Attempts} of {Max}); will retry next sweep.",
                        item.Notification.Id, item.Notification.EmailAttempts, MaxEmailAttempts);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return sent;
    }
}
