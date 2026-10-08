using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.Infrastructure.Notifications;

// Drains the EmailOutbox (ADR 0840), CLAIMING each row before it sends (ADR 0836).
//
// It used to scan the Notification rows themselves — `EmailedAt == null && EmailFailedAt == null` was the pending
// set — and the claim could not be put INTO that predicate without recording a failed send as sent, which is
// exactly the case ADR 0836 says needs a third state. The queue is that third state's home: Pending → Sending is
// the compare-and-swap, and the row is DELETED once the outcome is known, so an empty queue means "nothing owed"
// rather than "everything sent".
//
// One transaction per item, never per batch, so one bad send cannot discard the rest — and no row lock is held
// across an SMTP call, because `Sending` is the exclusion rather than a lock.
public sealed class EmailNotificationDispatcher : IEmailNotificationDispatcher
{
    // A bounded batch per pass keeps one sweep's work small; the rest are picked up next tick.
    private const int BatchSize = 200;

    // How many failures a notification gets before the system stops trying (ADR 0612). Five sweeps is minutes of
    // transient outage absorbed, while a genuinely dead address costs five sends rather than an unbounded number.
    private const int MaxEmailAttempts = 5;

    // How long a claim may stand before another sweep may take it back. A process killed between the send and the
    // bookkeeping leaves its row in `Sending` forever otherwise, and the email is then never retried — silence
    // being the one failure mode this whole path exists to avoid.
    //
    // THE PRICE IS A POSSIBLE DUPLICATE, stated rather than hidden: the send may have succeeded before the process
    // died, so a reclaim can deliver a second copy. That is the at-least-once contract this dispatcher has always
    // had (a failed send has always been retried), and a duplicate email is recoverable by a reader while a
    // missing one is not. Fifteen minutes is far longer than any send takes and short enough that a restart does
    // not strand mail for a working day.
    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(15);

    // The stable audit action code (mirrors Api.Controllers.AuditActions, which this layer can't reference —
    // the same arrangement RetentionService uses for its disposal event).
    private const string EmailAbandonedAction = "Notification.EmailAbandoned";
    private static readonly string[] TenantFilterOnly = ["TenantFilter"];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<EmailNotificationDispatcher> _logger;
    private readonly IAuditRecorder _audit;

    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopeFactory;

    // The AMBIENT tenant of this scope, which the real sender's TenantSmtpSettingsResolver reads to pick the
    // account (#1337). Set per message around the send (#1639): the sweep runs in one scope for every tenant,
    // and without it every tenant's mail went through the INSTALLATION's account, whatever the tenant had set.
    private readonly CurrentTenantAccessor? _ambientTenant;

    public EmailNotificationDispatcher(SimplArchiveDbContext dbContext, IEmailSender emailSender,
        ILogger<EmailNotificationDispatcher> logger, IAuditRecorder audit,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory,
        CurrentTenantAccessor? ambientTenant = null)
    {
        _dbContext = dbContext;
        _emailSender = emailSender;
        _logger = logger;
        _audit = audit;
        _scopeFactory = scopeFactory;
        _ambientTenant = ambientTenant;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        await ReclaimAbandonedClaimsAsync(cancellationToken);

        var pending = await ReadBatchAsync(cancellationToken);
        if (pending.Count == 0)
        {
            _logger.LogDebug("Email outbox is empty; nothing to send.");
            return 0;
        }

        // Email-channel preferences (ADR "Notification preferences"): a (user, type) with EmailEnabled = false is
        // suppressed. Only mutable types ever have a row, so the escalation types are never muted. Absence = on.
        //
        // Read HERE rather than at enqueue, deliberately: a preference read when the event happened would send
        // mail a reader had since muted.
        var recipientIds = pending.Select(p => p.RecipientUserId).Distinct().ToList();
        var muted = (await _dbContext.UserNotificationPreferences.IgnoreQueryFilters(TenantFilterOnly)
                .Where(p => recipientIds.Contains(p.UserId) && !p.EmailEnabled)
                .Select(p => new { p.UserId, p.Type })
                .ToListAsync(cancellationToken))
            .Select(p => (p.UserId, p.Type))
            .ToHashSet();

        var sent = 0;
        foreach (var item in pending)
        {
            // THE CLAIM, and it is IN the pending predicate — `State == Pending` is both what the read selected
            // and what this compare-and-swaps, which is the difference between a claim and mere bookkeeping. A
            // sweep in the other instance that got here first leaves 0 rows updated, and this one moves on without
            // sending anything.
            var won = await _dbContext.EmailOutbox
                .Where(o => o.Id == item.OutboxId && o.State == EmailOutboxState.Pending)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(o => o.State, EmailOutboxState.Sending)
                        .SetProperty(o => o.ClaimedAt, DateTimeOffset.UtcNow),
                    cancellationToken);

            if (won == 0)
            {
                _logger.LogDebug("Notification {NotificationId} was claimed by another sweep; skipping.", item.NotificationId);
                continue;
            }

            if (muted.Contains((item.RecipientUserId, item.Type)))
            {
                // Suppressed by the recipient's preference. The row goes and the notification is stamped with
                // NOTHING — so it says truthfully that it was never emailed, where the old code stamped EmailedAt
                // and left the row claiming an email nobody sent. Nothing re-enqueues it, because the enqueue
                // happens once, at insert.
                await CompleteAsync(item, emailedAt: null, failedAt: null, cancellationToken);
                _logger.LogDebug(
                    "Notification {NotificationId} is muted for {Recipient} by preference; dropped without sending.",
                    item.NotificationId, item.Email);
                continue;
            }

            try
            {
                // S/MIME enveloping (#1334; ADR 0856): THE MODULE FIRST, and where it answers it is the
                // ONLY source (ADR 0842) — then the #1332 column, then the sidecar's registry. The same
                // precedence the IMAP funnel now follows, so one rule serves both
                // (`ReaderCertificateSource`). With a certificate the subject goes GENERIC (headers cannot
                // encrypt) and the title moves into the encrypted body; without one, exactly yesterday's
                // plaintext.
                var certificates = await ReaderCertificatesForAsync(item, cancellationToken);
                var (subject, body) = certificates.Count == 0
                    ? (item.Title, item.Body)
                    : ("SimplArchive — new notification", $"{item.Title}\n\n{item.Body}");
                await SendAsTenantAsync(item.TenantId,
                    () => _emailSender.SendAsync(item.Email, item.DisplayName, subject, body, certificates, cancellationToken));

                await CompleteAsync(item, emailedAt: DateTimeOffset.UtcNow, failedAt: null, cancellationToken);
                sent++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                await RecordFailureAsync(item, e, cancellationToken);
            }
        }

        return sent;
    }

    /// <summary>
    /// Runs the send with the message's tenant as the ambient one, so the sender submits through THAT tenant's
    /// account (#1639), and restores whatever was there before, so nothing after the send runs as that tenant.
    /// </summary>
    private async Task SendAsTenantAsync(Guid tenantId, Func<Task> send)
    {
        if (_ambientTenant is null)
        {
            await send();
            return;
        }

        var previous = _ambientTenant.TenantId;
        _ambientTenant.TenantId = tenantId;
        try
        {
            await send();
        }
        finally
        {
            _ambientTenant.TenantId = previous;
        }
    }

    /// <summary>
    /// Puts claims nobody finished back in the queue, so a process killed mid-send does not strand mail.
    /// </summary>
    /// <remarks>
    /// <b>The cutoff is applied in MEMORY, not in SQL</b>, and that is a provider constraint rather than a
    /// preference: SQLite — the test provider — cannot compare a <c>DateTimeOffset</c> in SQL and throws
    /// <c>NotSupportedException</c> at query time, so <c>Where(o =&gt; o.ClaimedAt &lt; cutoff)</c> would pass
    /// against PostgreSQL and take the whole sweep down in the integration suite. The claimed set is tiny (rows
    /// being worked on right now), so reading it whole costs nothing.
    /// <para>
    /// A reclaim COUNTS as an attempt. It conflates "the process died" with "the send failed", which is imprecise
    /// on purpose: without it, a notification whose send reliably kills the worker is reclaimed forever, and the
    /// retry budget — the thing that stops a hopeless row stalling the queue — would never apply to it.
    /// </para>
    /// </remarks>
    private async Task ReclaimAbandonedClaimsAsync(CancellationToken cancellationToken)
    {
        var claimed = await _dbContext.EmailOutbox.AsNoTracking()
            .Where(o => o.State == EmailOutboxState.Sending)
            .Select(o => new { o.Id, o.ClaimedAt })
            .ToListAsync(cancellationToken);

        var cutoff = DateTimeOffset.UtcNow - ClaimLifetime;
        var stale = claimed.Where(o => o.ClaimedAt is null || o.ClaimedAt < cutoff).Select(o => o.Id).ToList();
        if (stale.Count == 0)
        {
            return;
        }

        var reclaimed = await _dbContext.EmailOutbox
            .Where(o => stale.Contains(o.Id) && o.State == EmailOutboxState.Sending)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(o => o.State, EmailOutboxState.Pending)
                    .SetProperty(o => o.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(o => o.Attempts, o => o.Attempts + 1),
                cancellationToken);

        // Warning, not Debug: a reclaim means a sweep died holding work, which an administrator should know about
        // — and it names the duplicate risk, because a reclaimed row whose send had actually succeeded is
        // delivered twice.
        _logger.LogWarning(
            "Reclaimed {Count} email claim(s) older than {Lifetime} — a previous sweep did not finish them. They "
            + "will be retried, which may deliver a second copy if the original send had already succeeded.",
            reclaimed, ClaimLifetime);
    }

    /// <summary>The batch to work on: pending rows with everything sending one needs, oldest first.</summary>
    /// <remarks>
    /// <b><c>AsNoTracking</c> is load-bearing</b> (ADR 0836): the claim below is an <c>ExecuteUpdate</c>, which the
    /// ChangeTracker cannot see, so a TRACKED row would keep the <c>Pending</c> it was loaded with and a later
    /// claim on the same context would be decided against a stale value.
    /// <para>
    /// The ORDER is computed in memory for the same reason the reclaim's cutoff is — SQLite refuses a
    /// <c>DateTimeOffset</c> in <c>ORDER BY</c> outright. This is the shape <c>SearchIndexWorker</c> already uses:
    /// project two columns over a table that is empty in the steady state, order them here, then fetch only the
    /// chosen keys.
    /// </para>
    /// </remarks>
    private async Task<List<Candidate>> ReadBatchAsync(CancellationToken cancellationToken)
    {
        var keys = (await _dbContext.EmailOutbox.AsNoTracking()
                .Where(o => o.State == EmailOutboxState.Pending)
                .Select(o => new { o.Id, o.EnqueuedAt })
                .ToListAsync(cancellationToken))
            .OrderBy(o => o.EnqueuedAt)
            .ThenBy(o => o.Id)
            .Take(BatchSize)
            .Select(o => o.Id)
            .ToList();

        if (keys.Count == 0)
        {
            return [];
        }

        // The tenant filter is ignored throughout: this sweep spans every tenant and has no ambient one.
        return await (
            from o in _dbContext.EmailOutbox.AsNoTracking()
            where keys.Contains(o.Id)
            join n in _dbContext.Notifications.IgnoreQueryFilters(TenantFilterOnly) on o.NotificationId equals n.Id
            join u in _dbContext.Users.IgnoreQueryFilters(TenantFilterOnly) on n.RecipientUserId equals u.Id
            join t in _dbContext.Tenants.IgnoreQueryFilters(TenantFilterOnly) on n.TenantId equals t.Id
            select new Candidate(
                o.Id,
                o.Attempts,
                n.Id,
                n.TenantId,
                n.RecipientUserId,
                n.Type,
                n.Title,
                n.Body,
                u.Email,
                u.DisplayName,
                u.SmimeCertificatePem,
                t.Name))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The certificates this recipient is addressed by — the module first, then the core's own sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A SCOPE PER ITEM, inside a sweep that deliberately has no ambient tenant.</b> This sweep spans
    /// every tenant (its batch query ignores the tenant filter and says so), while the module gate reads a
    /// tenant-scoped activation row — so the tenant has to be supplied per recipient. Mutating this
    /// dispatcher's own scope inside a cross-tenant loop was the alternative and is the shape that leaks:
    /// anything downstream caching "the" tenant would then serve one tenant's answer to the next. A child
    /// scope is thrown away with the item.
    /// </para>
    /// <para>
    /// <b>Failing to ask is NOT the same as an answer of none.</b> A module that throws is logged by
    /// <c>ModuleReaderCertificates</c> and returns no answer, and this path then sends PLAINTEXT — which is
    /// the notification contract (fail open, ADR 0813's precedent) and deliberately weaker than the strict
    /// content read, which refuses. Stated because the two paths read the same null and answer differently.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ReaderCertificatesForAsync(
        Candidate item, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        ((CurrentTenantAccessor)scope.ServiceProvider
            .GetRequiredService<SimplArchive.Application.Abstractions.ICurrentTenantAccessor>()).TenantId = item.TenantId;

        var fromModule = await scope.ServiceProvider
            .GetRequiredService<Modules.ModuleReaderCertificates>()
            .ForAsync(item.RecipientUserId, cancellationToken);

        // The module where one answers, else the reader's own column — never the encryption service's registry,
        // which is retired (ADR 0890).
        return Modules.ReaderCertificateSource.Resolve(fromModule, item.SmimeCertificatePem).Pems;
    }

    /// <summary>
    /// Ends this row's life: the queue row goes, and the notification records what happened — in ONE transaction.
    /// </summary>
    /// <remarks>
    /// Both statements or neither (ADR 0794). The order they would otherwise fail in matters: stamping the
    /// notification and then failing to delete the row leaves it claimable again, so the reclaim would send a
    /// SECOND copy of something already recorded as sent. Committing them together makes that unreachable.
    /// <para>
    /// <c>emailedAt</c> and <c>failedAt</c> are both null for a MUTED notification — nothing happened to it, and
    /// saying so is the whole point of moving the state off the row.
    /// </para>
    /// </remarks>
    private async Task CompleteAsync(
        Candidate item, DateTimeOffset? emailedAt, DateTimeOffset? failedAt, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (emailedAt is not null || failedAt is not null)
        {
            await _dbContext.Notifications.IgnoreQueryFilters(TenantFilterOnly)
                .Where(n => n.Id == item.NotificationId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(n => n.EmailedAt, emailedAt)
                        .SetProperty(n => n.EmailFailedAt, failedAt),
                    cancellationToken);
        }

        await _dbContext.EmailOutbox
            .Where(o => o.Id == item.OutboxId)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>A failed send: back in the queue with one more attempt spent, or abandoned.</summary>
    private async Task RecordFailureAsync(Candidate item, Exception e, CancellationToken cancellationToken)
    {
        var attempts = item.Attempts + 1;

        // A permanent rejection cannot succeed on retry — the mailbox does not exist, the domain does not
        // resolve — so spending the rest of the budget on it is four more useless sends and four more log
        // lines. A transient failure (server down, timeout, mailbox full) uses the budget.
        var permanent = e is PermanentEmailFailureException;
        var exhausted = attempts >= MaxEmailAttempts;

        if (!permanent && !exhausted)
        {
            await _dbContext.EmailOutbox
                .Where(o => o.Id == item.OutboxId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(o => o.State, EmailOutboxState.Pending)
                        .SetProperty(o => o.ClaimedAt, (DateTimeOffset?)null)
                        .SetProperty(o => o.Attempts, attempts),
                    cancellationToken);

            _logger.LogWarning(e, "Failed to email notification {NotificationId} (attempt {Attempts} of {Max}); will retry next sweep.",
                item.NotificationId, attempts, MaxEmailAttempts);
            return;
        }

        // Error, not Warning: the retries are routine, giving up is the thing an administrator has to act on —
        // a wrong address to correct, or a mail server that has been down for hours.
        _logger.LogError(e,
            "Gave up emailing notification {NotificationId} to {Recipient} after {Attempts} attempt(s) ({Reason}); the in-app notification is unaffected.",
            item.NotificationId, item.Email, attempts, permanent ? "permanently rejected" : "retry budget exhausted");

        // …and in the product, not only in a log nobody may be reading. The ACTOR is passed explicitly (#1312):
        // this sweep has no ambient principal any more than it has an ambient tenant, and RecordAsync drops the
        // event when it cannot resolve one. The System actor with a descriptive subsystem name is the
        // RetentionService idiom.
        await _audit.RecordForActorAsync(
            SimplArchive.Domain.Audit.AuditActorType.System, Guid.Empty, "Notification email",
            item.TenantId, EmailAbandonedAction, "Notification", item.NotificationId, item.Title,
            $"{(permanent ? "Permanently rejected" : "Retry budget exhausted")} after {attempts} attempt(s): {e.Message}",
            cancellationToken);

        await CompleteAsync(item, emailedAt: null, failedAt: DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>One row's worth of everything sending it needs, read once so the loop makes no further queries.</summary>
    private sealed record Candidate(
        Guid OutboxId,
        int Attempts,
        Guid NotificationId,
        Guid TenantId,
        Guid RecipientUserId,
        Domain.Notifications.NotificationType Type,
        string Title,
        string Body,
        string Email,
        string DisplayName,
        string? SmimeCertificatePem,
        string TenantName);
}
