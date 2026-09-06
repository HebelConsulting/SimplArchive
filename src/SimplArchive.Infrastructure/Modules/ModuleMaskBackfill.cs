using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Heals a module UPGRADE's new masks and fields onto tenants that activated an EARLIER version (ADR 0757).
/// <para>
/// A module's masks are seeded once, at ACTIVATION (<see cref="ModuleActivationService"/>). But a module is
/// updated over time: a later version adds a mask or a field, and every tenant that activated the older
/// version silently misses it — the new feature fails with <c>MASK_NOT_FOUND</c> until someone re-activates.
/// This is the module analogue of the core's own startup well-known-mask backfill (the "a fact added later
/// reaches only new tenants unless the heal carries it too" lesson): <see cref="ModuleMaskSeeder"/> is
/// idempotent and heals (it adds a missing mask and adds optional fields, and refuses a required field on a
/// worn mask — loudly), so re-running it for every active tenant per startup backfills the newer shapes.
/// </para>
/// <para>
/// Guarded per tenant: a single misbehaving module — one that ships a required field onto an existing mask,
/// say — must not brick the host at startup for every tenant. The failure is logged for an administrator and
/// the other tenants and modules still heal; the module's feature stays unavailable there until it is fixed,
/// exactly as it was before this ran.
/// </para>
/// </summary>
public static class ModuleMaskBackfill
{
    public static async Task HealAsync(
        SimplArchiveDbContext dbContext,
        ModuleMaskSeeder seeder,
        IReadOnlyList<IIndustryModule> modules,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var loadedById = modules.ToDictionary(m => m.ModuleId, StringComparer.Ordinal);

        // Every activation, across every tenant — the query runs before any request, so there is no ambient
        // tenant; the seeder writes tenant-scoped rows with an explicit TenantId, exactly as the well-known
        // backfill does.
        var activations = await dbContext.ModuleActivations
            .IgnoreQueryFilters(["TenantFilter"])
            .Select(a => new { a.TenantId, a.ModuleId })
            .ToListAsync(cancellationToken);

        foreach (var activation in activations)
        {
            if (!loadedById.TryGetValue(activation.ModuleId, out var module))
            {
                continue; // an activation for a module this deployment no longer loads — nothing to seed
            }

            try
            {
                await seeder.SeedAsync(module, activation.TenantId, cancellationToken);
            }
            catch (Exception ex)
            {
                // Deliberately broad: a defensive backstop so one module's bad upgrade cannot take the host
                // down for every tenant (the same posture as the process-level unhandled-exception backstop).
                logger.LogError(ex,
                    "Module {ModuleId}: mask backfill failed for tenant {TenantId} — its newer masks or fields "
                    + "were not healed, so its feature may be unavailable there until the module is corrected.",
                    activation.ModuleId, activation.TenantId);
            }
        }
    }
}
