using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Modules;

/// <summary>
/// Records whether a module's on-demand content source is refreshing, and tells the tenant's administrators
/// when one has stopped (ADR 0811).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> A populate hook degrades to "serve what is already filed" when its fetch
/// fails — deliberately, because a failed enrichment must not break somebody else's read. The price is that
/// the only record is a log line in the OPERATOR's collector, which a tenant administrator cannot see. So a
/// dead weather feed looks exactly like a quiet one, indefinitely, inside an installation where every other
/// check is green.
/// </para>
/// <para>
/// <b>One recorder for every invocation path.</b> A hook runs from a client open and from a protocol read,
/// and a count that saw only one of them would state the wrong thing — "failing since 14:05" has to mean
/// every attempt, or it is not a duration at all.
/// </para>
/// </remarks>
public sealed class ModuleContentHealthRecorder(
    SimplArchiveDbContext dbContext,
    ILogger<ModuleContentHealthRecorder> logger)
{
    /// <summary>
    /// How many consecutive failures make an episode worth an administrator's attention.
    /// </summary>
    /// <remarks>
    /// Three, and it is already conservative rather than twitchy: a hook only runs when somebody opens the
    /// folder AND the content has expired, so with a two-hour weather TTL three in a row is most of a working
    /// day during which the folder has been answering with nothing. A threshold of one would notify on the
    /// provider's ordinary bad minute, which is the noise that gets a notification type muted.
    /// </remarks>
    public const int NotifyAfterConsecutiveFailures = 3;

    /// <summary>The source refreshed. Deletes the episode — absence is how "healthy" is stored.</summary>
    public async Task RecordSuccessAsync(
        Guid tenantId, string moduleId, string machineId, Guid subjectDocumentId, CancellationToken cancellationToken)
    {
        var row = await FindAsync(tenantId, moduleId, machineId, subjectDocumentId, cancellationToken);
        if (row is null)
        {
            return;   // the overwhelmingly common path: nothing was broken, so there is nothing to clear
        }

        dbContext.ModuleContentHealth.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Module {ModuleId} content source {MachineId} on folder {FolderId} recovered after {Failures} failure(s).",
            moduleId, machineId, subjectDocumentId, row.ConsecutiveFailures);
    }

    /// <summary>
    /// The source failed. Opens or extends the episode, and notifies the tenant's administrators the once, on
    /// the read that crosses the threshold.
    /// </summary>
    public async Task RecordFailureAsync(
        Guid tenantId, string moduleId, string machineId, Guid subjectDocumentId, string folderName,
        string error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // A MESSAGE, never a payload (ADR 0626). The exception's own sentence describes what we failed to do;
        // what the provider sent back is the thing most likely to carry a credential or a licensed artefact,
        // and this string is rendered onto an administrator's screen.
        var message = Truncate(error, 500);

        var row = await FindAsync(tenantId, moduleId, machineId, subjectDocumentId, cancellationToken);
        if (row is null)
        {
            row = new ModuleContentHealth
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ModuleId = moduleId,
                MachineId = machineId,
                SubjectDocumentId = subjectDocumentId,
                ConsecutiveFailures = 0,
                FirstFailureAt = now,
            };
            dbContext.ModuleContentHealth.Add(row);
        }

        row.ConsecutiveFailures++;
        row.LastFailureAt = now;
        row.LastError = message;

        var crossing = row.NotifiedAt is null && row.ConsecutiveFailures >= NotifyAfterConsecutiveFailures;
        if (crossing)
        {
            row.NotifiedAt = now;
            await NotifyAdminsAsync(tenantId, moduleId, folderName, row, cancellationToken);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The expected case is the race: two instances failed the same source at the same moment and both
            // tried to OPEN the episode, and the unique index refuses the second. That resolves itself
            // correctly — one row exists, one notification was sent — and the loser must drop its duplicate
            // rather than retry, since retrying would re-count a failure the winner already counted.
            //
            // WARNING WITH THE EXCEPTION, never Debug and never bare. This catch is wide enough to swallow a
            // defect of OURS — the first version did exactly that, hiding a foreign-key violation for a whole
            // debugging round while the in-memory entity still read as though the save had worked. A silent
            // catch on a save is how a health record that never persists looks identical to a healthy source,
            // which is the precise failure this whole feature exists to end.
            //
            // Not rethrown: this is bookkeeping ABOUT somebody else's failure and must not become a second
            // failure of its own. Visible, not fatal.
            logger.LogWarning(ex,
                "Could not persist the content-health record for {ModuleId}/{MachineId} on folder {FolderId}. "
                + "If this is not a concurrent writer opening the same episode, the health state for this "
                + "source is NOT being recorded and an administrator will not be told it is failing.",
                moduleId, machineId, subjectDocumentId);
        }
    }

    private Task<ModuleContentHealth?> FindAsync(
        Guid tenantId, string moduleId, string machineId, Guid subjectDocumentId, CancellationToken cancellationToken) =>
        dbContext.ModuleContentHealth
            .IgnoreQueryFilters()   // a protocol read has a tenant, but a sweep or a worker may not
            .Where(h => h.TenantId == tenantId
                && h.ModuleId == moduleId
                && h.MachineId == machineId
                && h.SubjectDocumentId == subjectDocumentId)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task NotifyAdminsAsync(
        Guid tenantId, string moduleId, string folderName, ModuleContentHealth row, CancellationToken cancellationToken)
    {
        // Each active tenant admin directly, NOT through INotificationService: its self-skip drops the acting
        // principal, and here the actor is the module's own service principal — which would silently reduce
        // the fan-out in a way nobody could see. The storage-quota warning writes admin notifications the same
        // way, for the same reason.
        var admins = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.IsTenantAdmin && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (admins.Count == 0)
        {
            // Worth a Warning rather than silence: a tenant with no active administrator cannot be told
            // anything, and that is itself the finding.
            logger.LogWarning(
                "Module {ModuleId} content has been failing since {Since} but the tenant has no active administrator to tell.",
                moduleId, row.FirstFailureAt);
            return;
        }

        var title = $"{moduleId}: content is not refreshing";
        var body = $"\"{folderName}\" has failed to refresh {row.ConsecutiveFailures} times in a row since "
            + $"{row.FirstFailureAt:yyyy-MM-dd HH:mm} UTC. The archive is serving whatever is already filed there. "
            + $"Last error: {row.LastError}";

        var now = DateTimeOffset.UtcNow;
        foreach (var adminId in admins)
        {
            dbContext.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RecipientUserId = adminId,
                Type = NotificationType.ModuleContentFailing,
                Title = title,
                Body = body,
                // The folder itself, so the notification opens the thing it is about rather than leaving an
                // administrator to search for a name out of a sentence.
                DocumentId = row.SubjectDocumentId,
                CreatedAt = now,
            });
        }

        logger.LogWarning(
            "Module {ModuleId} content source {MachineId} on folder {FolderId} has failed {Failures} times since {Since}; "
            + "notified {Admins} administrator(s). Trace carries the exchange with the provider.",
            moduleId, row.MachineId, row.SubjectDocumentId, row.ConsecutiveFailures, row.FirstFailureAt, admins.Count);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
