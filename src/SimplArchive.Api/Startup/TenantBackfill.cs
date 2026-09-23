using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Startup;

/// <summary>
/// The per-tenant startup backfills (#1340), extracted from Program.cs so the loop that made them
/// quadratic is testable.
/// </summary>
/// <remarks>
/// <para>
/// <b>The change tracker is cleared after every tenant, and that is the whole point of this class.</b>
/// One <see cref="SimplArchiveDbContext"/> served the entire loop, so tenant N's seed carried everything
/// tracked from tenants 1…N−1 — and each <c>SaveChangesAsync</c> runs ~10 validation/sync methods that
/// every call <c>ChangeTracker.Entries&lt;T&gt;()</c>, forcing a full <c>DetectChanges()</c> over that
/// accumulated graph. The cost is therefore quadratic in tenant count, and each sweep allocates.
/// </para>
/// <para>
/// Measured before the fix (#1340): a host booting against 182 tenants / 4 378 masks pegged one core at
/// ~98% for over an hour with flat RSS — a continuous allocate→GC loop whose managed stack was
/// <c>EnsureAssignabilityAsync → SaveChangesAsync → SyncBlockDocumentsAsync → ChangeTracker.Entries →
/// ValueComparer&lt;DateTimeOffset&gt;.Equals</c>. It surfaced in the E2E suite because
/// <c>RealtimeBackplaneTests</c> is the only test that boots a SECOND host mid-run, by which point the
/// suite has created hundreds of tenants — but the cost is a production one: every installation pays it
/// on every boot, and it degrades superlinearly.
/// </para>
/// <para>
/// Clearing is safe because each tenant's backfill is self-contained: it loads what it needs and saves
/// before the next begins, so nothing needs to stay tracked across iterations.
/// </para>
/// </remarks>
public static class TenantBackfill
{
    /// <summary>
    /// Idempotent well-known-mask backfill for every EXISTING tenant — a newly added well-known mask is
    /// otherwise seeded only at tenant provisioning, so tenants created before an upgrade silently miss it
    /// (the demo stack's Personal/Notes folder came out maskless because of exactly that).
    /// </summary>
    public static async Task SeedWellKnownMasksAsync(
        SimplArchiveDbContext dbContext,
        IWellKnownMaskSeeder maskSeeder,
        CancellationToken cancellationToken = default)
    {
        foreach (var tenantId in await dbContext.Tenants.Select(t => t.Id).ToListAsync(cancellationToken))
        {
            await maskSeeder.EnsureWellKnownMasksAsync(tenantId);

            // Per tenant, not once at the end: the point is to keep the tracked graph BOUNDED while the
            // loop runs, which is what makes the work linear (#1340).
            dbContext.ChangeTracker.Clear();
        }
    }
}
