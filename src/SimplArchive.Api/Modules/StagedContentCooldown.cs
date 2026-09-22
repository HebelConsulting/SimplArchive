using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Modules;

/// <summary>
/// The populate hook's cooldown (ADRs 0810/0814): a folder that already holds unexpired staged content is
/// current, so the hook is not run — no upstream request leaves the process. ONE implementation, asked by
/// every path that can invoke the hook (the protocol runner and the clients' transition POSTs), because two
/// copies of this question is how the folder-open path came to have no cooldown at all (#1309).
/// </summary>
public static class StagedContentCooldown
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
}
