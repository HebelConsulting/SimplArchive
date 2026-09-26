using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The escalate half of ADR 0740's ladder: compares every activation's CURRENT escalation step (derived
/// from <see cref="ModuleActivationPolicy"/>) with the step already announced, and on an upward cross
/// notifies the tenant's active admins — the storage-soft-quota shape, including its reasons: every admin
/// directly (no self-skip), pre-rendered Title/Body, and a non-mutable type the dispatcher always emails.
/// A downward move (a renewal was filed) just re-arms the level, silently.
/// </summary>
public sealed class ModuleEscalationService
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ILogger<ModuleEscalationService> _logger;

    public ModuleEscalationService(SimplArchiveDbContext dbContext, ILogger<ModuleEscalationService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>Sweeps every tenant's activations (the worker's ambient tenant is nobody). Returns how
    /// many escalation notifications were written, for the worker's log line.</summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // NO TRACKING, and that is load-bearing rather than an optimisation. The only write to an activation
        // here is the ExecuteUpdate in ClaimLevelAsync, which the ChangeTracker cannot see — so a TRACKED
        // activation would keep the level it was loaded with, and a second SweepAsync on the same context would
        // read that stale value, compare-and-swap against it, and lose a claim it should have won. An existing
        // test caught exactly that: three rungs crossed in sequence on one context announced once.
        var activations = await _dbContext.ModuleActivations
            .IgnoreQueryFilters(["TenantFilter"])
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var notified = 0;
        foreach (var activation in activations)
        {
            var level = ModuleActivationPolicy.EscalationLevelFor(activation, now);
            if (level == activation.EscalationLevel)
            {
                continue;
            }

            if (level < activation.EscalationLevel)
            {
                // A renewal was filed — re-arm, silently. Claimed like the upward cross, and for a duller
                // reason: two sweeps writing the same lower level is harmless, but the SECOND one's write is
                // refused by the concurrency token (ModuleActivation is IConcurrencyTracked), and that refusal
                // throws out the whole batch — including other tenants' escalations that had already succeeded
                // in it. Nothing here is duplicated; what is lost is everything else.
                await ClaimLevelAsync(activation, level, cancellationToken);
                continue;
            }

            // ONE activation, ONE transaction: the level change and the admins' notifications commit together,
            // and no row lock is held across activations (a batch-wide transaction is how two sweeps deadlock).
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (!await ClaimLevelAsync(activation, level, cancellationToken))
            {
                // The other instance's sweep announced this step. Normal with two instances (ADR 0808).
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            var admins = await _dbContext.Users
                .IgnoreQueryFilters(["TenantFilter"])
                .Where(u => u.TenantId == activation.TenantId && u.IsTenantAdmin && u.IsActive)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            var (title, body) = Announce(activation, level);
            foreach (var adminId in admins)
            {
                _dbContext.Notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    TenantId = activation.TenantId,
                    RecipientUserId = adminId,
                    Type = NotificationType.ModuleLicenseEscalation,
                    Title = title,
                    Body = body,
                    CreatedAt = now,
                });
                notified++;
            }

            // The level is NOT written here — the claim above already did it, atomically. Writing it again
            // would be a second update from state the ChangeTracker cannot see an ExecuteUpdate has moved.
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Module {ModuleId} in tenant {TenantId} escalated to level {Level}; {Admins} admin(s) notified.",
                activation.ModuleId, activation.TenantId, level, admins.Count);
        }

        return notified;
    }

    /// <summary>
    /// Moves this activation to <paramref name="level"/> for THIS sweep, atomically, and says whether it was won.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A compare-and-swap on the level this sweep read, so the database picks the winner and the loser gets 0
    /// rows (#1425). The same mechanism as the reminder and workflow-escalation sweeps.
    /// </para>
    /// <para>
    /// <b>What it prevents here is lost work, not a duplicate.</b> <see cref="ModuleActivation"/> is
    /// <c>IConcurrencyTracked</c>, so the losing sweep's tracked write was already refused — but that refusal
    /// is a <c>DbUpdateConcurrencyException</c> from a <c>SaveChanges</c> that covered EVERY activation in the
    /// sweep, so one contended row discarded every other tenant's escalation in the same batch, and the worker
    /// logged a generic warning that named neither. Two instances each run this on a timer (ADR 0808), so that
    /// was not an accident but the arrangement.
    /// </para>
    /// </remarks>
    private async Task<bool> ClaimLevelAsync(
        ModuleActivation activation, int level, CancellationToken cancellationToken) =>
        await _dbContext.ModuleActivations
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(a => a.Id == activation.Id && a.EscalationLevel == activation.EscalationLevel)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.EscalationLevel, level), cancellationToken) == 1;

    private static (string Title, string Body) Announce(ModuleActivation activation, int level)
    {
        var end = activation.SupportContractEndDate.ToString("yyyy-MM-dd");
        var off = ModuleActivationPolicy.DeactivatesAt(activation).ToString("yyyy-MM-dd");
        return level switch
        {
            1 => ($"Module '{activation.ModuleId}': support contract ends soon",
                $"The support contract for module '{activation.ModuleId}' ends on {end}. " +
                "File the renewed license before then to keep the module running without interruption."),
            2 => ($"Module '{activation.ModuleId}': support contract has ended",
                $"The support contract for module '{activation.ModuleId}' ended on {end}. " +
                $"The module keeps running on grace until {off} — file the renewed license before that date."),
            _ => ($"Module '{activation.ModuleId}' has been deactivated",
                $"The grace period for module '{activation.ModuleId}' ran out on {off} and its behaviour is now off. " +
                "Documents and masks remain fully usable; filing a renewed license reactivates the module."),
        };
    }
}
