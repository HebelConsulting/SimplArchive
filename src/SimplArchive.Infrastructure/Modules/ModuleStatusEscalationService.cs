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
    private readonly ModuleNoticeDelivery _delivery;
    private readonly IServiceProvider _services;
    private readonly ModuleReadModelCatalog _readModels;
    private readonly ILogger<ModuleStatusEscalationService> _logger;

    public ModuleStatusEscalationService(
        SimplArchiveDbContext dbContext,
        CurrentTenantAccessor tenantAccessor,
        StateMachineEngine engine,
        StateMachineCatalog catalog,
        INotificationService notifications,
        ILogger<ModuleStatusEscalationService> logger,
        ModuleNoticeDelivery delivery,
        IServiceProvider services,
        ModuleReadModelCatalog? readModels = null)
    {
        _services = services;
        _readModels = readModels ?? ModuleReadModelCatalog.Empty;
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _engine = engine;
        _catalog = catalog;
        _notifications = notifications;
        _delivery = delivery;
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
                    .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => v.Id, (d, v) => new { d.Id, v.MaskId })
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

    /// <summary>
    /// One subject's escalation, CLAIMED and ATOMIC (#1638, ADR 0836): in ONE transaction, a Postgres advisory lock on
    /// (machine, status, subject) is taken first, then the module's handler runs (writing its once-only marker) and its
    /// notices are delivered (the notification, or the external outbox row). The other instance waits on the lock, then
    /// reads the committed marker and sends nothing; a crash leaves either everything or nothing, never a marker with no
    /// notice. The engine's transaction is the same machinery, joined (ADR 0902).
    /// </summary>
    private async Task<int> EscalateOneAsync(string machineId, string statusName, Guid subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var machine = _catalog.Machines[machineId];
        var external = machine.ExternalEscalations.Contains(statusName);
        try
        {
            return await ModuleTransaction.RunAsync(_dbContext, _readModels, _services, async () =>
            {
                await ClaimAsync(machineId, statusName, subjectId, cancellationToken);

                // Acts as the module, evaluates the status, and runs the handler only when the status holds: a subject
                // the module cannot see reads empty and never escalates.
                var notices = await _engine.ExecuteEscalationAsync(machineId, statusName, subjectId, now, cancellationToken);

                // One delivery rule with an action's notices (ABI 1.10, ADR 0902): a user gets the in-app path; any other
                // address only when this STATUS was declared to reach external recipients, else a Warning naming who.
                var sent = 0;
                foreach (var notice in notices)
                {
                    if (await _delivery.DeliverAsync(machine.ModuleId ?? machineId, notice.RecipientEmail, notice.Title, notice.Message,
                            notice.ReplyTo, notice.AttachmentDocumentId, subjectId, external, cancellationToken))
                    {
                        sent++;
                    }
                }

                return sent;
            }, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One subject's failing handler must not sink the sweep; nothing of it was committed. Trace carries the
            // exchange (ADR 0626).
            _logger.LogWarning(e, "Escalation {Machine}/{Status} on subject {Subject} threw; nothing was sent or recorded.", machineId, statusName, subjectId);
            return 0;
        }
    }

    /// <summary>
    /// The claim (#1638): a transaction-scoped advisory lock on a stable 64-bit key of (machine, status, subject), released
    /// at commit or rollback. Postgres only; under SQLite (the tests' provider) there is one connection and nothing to
    /// contend with.
    /// </summary>
    private async Task ClaimAsync(string machineId, string statusName, Guid subjectId, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsNpgsql())
        {
            return;
        }

        var key = LockKey(machineId, statusName, subjectId);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    /// <summary>A stable lock key: the first 8 bytes of SHA-256 over the triple. Stable across instances and restarts,
    /// unlike string.GetHashCode, which is randomised per process.</summary>
    public static long LockKey(string machineId, string statusName, Guid subjectId) =>
        BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"module-escalation|{machineId}|{statusName}|{subjectId:D}")), 0);
}
