using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Modules;

/// <summary>
/// The populate hook's cooldown (ADRs 0810/0814, #1307): ONE implementation, asked by every path that can
/// invoke the hook — the protocol runner and the clients' transition POSTs — because two copies of this
/// question is how the folder-open path came to have no cooldown at all (#1309). Two clocks, one per
/// content shape:
/// <list type="bullet">
/// <item><b>Ephemeral content</b>: a folder already holding a child with an unexpired <c>ExpiresAt</c> is
/// current — the staged content's own expiry is the clock, no table needed (ADR 0810).</item>
/// <item><b>Durable content</b> (ABI 0.28): a calendar of real entries never expires, so a transition
/// declaring a minimum refresh interval is clocked by its recorded last ATTEMPT — success or failure,
/// because the outbound request is the thing being rate-limited.</item>
/// </list>
/// </summary>
public static class PopulateCooldown
{
    /// <summary>Whether <paramref name="folderId"/> holds a child with an unexpired <c>ExpiresAt</c> —
    /// the staged content's own expiry is the clock; there is no cooldown table to go stale.</summary>
    public static async Task<bool> HoldsUnexpiredContentAsync(
        SimplArchiveDbContext dbContext, Guid folderId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The null-check is the SQL predicate and the COMPARISON is in memory, exactly as the sweep worker
        // that purges the same rows does it: SQLite cannot compare a DateTimeOffset in SQL, and the model
        // runs on both providers (ADR 0002's parity rule). Written as one predicate it throws at QUERY time.
        var expiries = await dbContext.Documents
            .Where(d => d.ParentId == folderId && d.ExpiresAt != null)
            .Select(d => d.ExpiresAt)
            .ToListAsync(cancellationToken);

        return expiries.Any(e => e > now);
    }

    /// <summary>Whether the recorded last attempt for (<paramref name="machineId"/>,
    /// <paramref name="folderId"/>) is younger than <paramref name="interval"/> — the durable-content
    /// clock (ABI 0.28). No row means never attempted.</summary>
    public static async Task<bool> AttemptWithinIntervalAsync(
        SimplArchiveDbContext dbContext, string machineId, Guid folderId, TimeSpan interval,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Same provider-parity split as above: fetch the instant, compare in memory.
        var lastAttempt = await dbContext.ModulePopulateAttempts
            .Where(a => a.MachineId == machineId && a.SubjectDocumentId == folderId)
            .Select(a => (DateTimeOffset?)a.LastAttemptAt)
            .FirstOrDefaultAsync(cancellationToken);

        return lastAttempt is { } at && at + interval > now;
    }

    /// <summary>
    /// Stamps the attempt clock for (<paramref name="machineId"/>, <paramref name="folderId"/>) — called
    /// immediately BEFORE the hook runs, so a failing source is rate-limited exactly like a healthy one,
    /// and independently of the engine's transaction, so a rolled-back handler does not un-stamp it.
    /// </summary>
    public static async Task RecordAttemptAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, string machineId, Guid folderId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.ModulePopulateAttempts
            .FirstOrDefaultAsync(a => a.MachineId == machineId && a.SubjectDocumentId == folderId, cancellationToken);
        if (row is null)
        {
            row = new ModulePopulateAttempt
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MachineId = machineId,
                SubjectDocumentId = folderId,
            };
            dbContext.ModulePopulateAttempts.Add(row);
        }

        row.LastAttemptAt = now;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two instances racing the first stamp: the unique index lets exactly one insert win, and the
            // loser's fact — "an attempt happened now" — is already recorded by the winner. Detach and move
            // on rather than failing the read that triggered the populate.
            dbContext.Entry(row).State = EntityState.Detached;
        }
    }
}
