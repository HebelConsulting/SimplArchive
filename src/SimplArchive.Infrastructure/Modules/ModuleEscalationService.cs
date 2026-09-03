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
        var activations = await _dbContext.ModuleActivations
            .IgnoreQueryFilters(["TenantFilter"])
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
                activation.EscalationLevel = level; // a renewal was filed — re-arm, silently
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

            activation.EscalationLevel = level;
            _logger.LogInformation(
                "Module {ModuleId} in tenant {TenantId} escalated to level {Level}; {Admins} admin(s) notified.",
                activation.ModuleId, activation.TenantId, level, admins.Count);
        }

        if (activations.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return notified;
    }

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
