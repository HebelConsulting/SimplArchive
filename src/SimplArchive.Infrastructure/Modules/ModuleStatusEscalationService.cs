using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The FIRING half of ADR 0753's escalation ladder (flight-school #3/#5): a background sweep that, per active
/// module and tenant, finds the subjects sitting in a status the module marked worth a reminder and hands each
/// to the module's escalation handler (ABI 0.5). The handler — running act-as-the-module, in the engine's
/// transaction — walks the subject's own graph to name who to tell and what to say, and reads/writes its own
/// idempotency marker so it warns once. The core does only the two things the module cannot: enumerate every
/// tenant's subjects (the worker's ambient tenant is nobody), and resolve a recipient e-mail to a tenant user
/// it can file an in-app notification for. Distinct from <see cref="ModuleEscalationService"/>, which escalates
/// a module's own LICENCE lifecycle (ADR 0740) rather than the statuses a module declares over its documents.
/// </summary>
public sealed class ModuleStatusEscalationService
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly CurrentTenantAccessor _tenantAccessor;
    private readonly StateMachineEngine _engine;
    private readonly StateMachineCatalog _catalog;
    private readonly INotificationService _notifications;
    private readonly ILogger<ModuleStatusEscalationService> _logger;

    public ModuleStatusEscalationService(
        SimplArchiveDbContext dbContext,
        CurrentTenantAccessor tenantAccessor,
        StateMachineEngine engine,
        StateMachineCatalog catalog,
        INotificationService notifications,
        ILogger<ModuleStatusEscalationService> logger)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _engine = engine;
        _catalog = catalog;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>Sweeps every tenant's active modules. Returns how many escalation notifications were written,
    /// for the worker's one log line.</summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // Only machines that actually DECLARE an escalation are worth a sweep — everything else is skipped
        // before a single query. Module-declared machines carry a ModuleId (null is test-only, ungated).
        var escalating = _catalog.Machines.Values
            .Where(m => m.ModuleId is not null && m.Escalations.Count > 0)
            .ToList();
        if (escalating.Count == 0)
        {
            return 0;
        }

        // Every activation, across all tenants — the worker has no ambient tenant, so the filter is bypassed
        // here and re-imposed per tenant below (the accessor the DbContext reads is set at the top of the loop).
        var activations = await _dbContext.ModuleActivations
            .IgnoreQueryFilters(["TenantFilter"])
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var tenantGroup in activations.GroupBy(a => a.TenantId))
        {
            _tenantAccessor.TenantId = tenantGroup.Key;
            var activeModuleIds = tenantGroup
                .Where(a => ModuleActivationPolicy.IsActive(a, now))
                .Select(a => a.ModuleId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var machine in escalating.Where(m => activeModuleIds.Contains(m.ModuleId!)))
            {
                var subjectIds = await _dbContext.Documents
                    .Where(d => d.MaskVersionId != null)
                    .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => new { d.Id, v.MaskId })
                    .Where(x => x.MaskId == machine.SubjectMaskId)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);

                foreach (var statusName in machine.Escalations.Keys)
                {
                    foreach (var subjectId in subjectIds)
                    {
                        sent += await EscalateOneAsync(machine.MachineId, statusName, subjectId, now, cancellationToken);
                    }
                }
            }
        }

        return sent;
    }

    private async Task<int> EscalateOneAsync(string machineId, string statusName, Guid subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<EscalationNotice> notices;
        try
        {
            // Acts as the module, evaluates the status, and runs the handler in the engine's transaction only
            // when the status holds — a subject the module cannot see reads empty and never escalates.
            notices = await _engine.ExecuteEscalationAsync(machineId, statusName, subjectId, now, cancellationToken);
        }
        catch (Exception e)
        {
            // One subject's failing handler must not sink the sweep — the whole exchange is at Trace (ADR 0626).
            _logger.LogWarning(e, "Escalation {Machine}/{Status} on subject {Subject} threw; skipping it.", machineId, statusName, subjectId);
            return 0;
        }

        var sent = 0;
        foreach (var notice in notices)
        {
            var normalized = notice.RecipientEmail.Trim().ToUpperInvariant();
            var recipientId = await _dbContext.Users
                .Where(u => u.NormalizedEmail == normalized)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (recipientId is not { } userId)
            {
                // An external instructor with no account here cannot receive an in-app notice; naming who was
                // skipped (not the message) is the Warning ADR 0626 asks for when we silently do less.
                _logger.LogWarning("Module escalation recipient {Email} is not a user in tenant {Tenant}; the reminder was not delivered.", notice.RecipientEmail, _tenantAccessor.TenantId);
                continue;
            }

            await _notifications.NotifyAsync(userId, NotificationType.ModuleStatusEscalation, notice.Title, notice.Message, subjectId, cancellationToken);
            sent++;
        }

        return sent;
    }
}
