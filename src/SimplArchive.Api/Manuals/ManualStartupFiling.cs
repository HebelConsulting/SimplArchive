using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Manuals;

/// <summary>
/// The filing moments that are not a request (ADR 0891): every startup, for every tenant — SimplArchive's own manual
/// and the manual of every module the tenant activated — and a tenant's founding through the API.
/// </summary>
/// <remarks>
/// Each tenant runs in its own scope with its tenant set, so the tenant filter answers for it and one tenant's
/// tracked state never rides into the next. A failure is logged and the next tenant proceeds: arbitrary module code
/// (its manual's <c>Open</c>) runs here, and one bad package must not take the host down for everyone. Runs at the
/// END of the startup scope, after the seeders, so a tenant the demo seeders just founded gets its manuals on its
/// first start rather than its second.
/// </remarks>
public static class ManualStartupFiling
{
    /// <summary>The startup step: every tenant, the loaded modules, logged under the filer's category.</summary>
    public static Task FileEverywhereAsync(IServiceProvider root) =>
        FileEverywhereAsync(root,
            [.. root.GetRequiredService<IReadOnlyList<SimplArchive.Infrastructure.Modules.ModuleLoader.LoadedModule>>().Select(m => m.Module)],
            root.GetRequiredService<ILoggerFactory>().CreateLogger("SimplArchive.Api.Manuals.ManualFiler"));

    public static async Task FileEverywhereAsync(
        IServiceProvider root, IReadOnlyList<IIndustryModule> modules, ILogger logger, CancellationToken cancellationToken = default)
    {
        List<Guid> tenants;
        List<(Guid TenantId, string ModuleId)> activations;
        using (var scope = root.CreateScope())
        {
            // Before any request: no ambient tenant, so the tenant filter is bypassed BY NAME for these enumerations.
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            tenants = await db.Tenants
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
            activations = (await db.ModuleActivations.IgnoreQueryFilters(["TenantFilter"])
                    .Select(a => new { a.TenantId, a.ModuleId })
                    .ToListAsync(cancellationToken))
                .Select(a => (a.TenantId, a.ModuleId))
                .ToList();
        }

        var withManual = modules.Where(m => m.Manual is not null).ToDictionary(m => m.ModuleId, StringComparer.Ordinal);
        foreach (var tenantId in tenants)
        {
            var mine = activations.Where(a => a.TenantId == tenantId && withManual.ContainsKey(a.ModuleId))
                .Select(a => withManual[a.ModuleId])
                .ToList();
            await FileForTenantAsync(root, tenantId, mine, logger, cancellationToken);
        }
    }

    /// <summary>Files the core manual and the given modules' manuals for one tenant, each check in a fresh scope.</summary>
    public static async Task FileForTenantAsync(
        IServiceProvider root, Guid tenantId, IReadOnlyList<IIndustryModule> modules, ILogger logger, CancellationToken cancellationToken = default)
    {
        await GuardedAsync(root, tenantId, "SimplArchive", filer => filer.FileCoreManualAsync(cancellationToken), logger);
        foreach (var module in modules)
        {
            await GuardedAsync(root, tenantId, module.ModuleId, filer => filer.FileModuleManualAsync(module, cancellationToken), logger);
        }
    }

    private static async Task GuardedAsync(
        IServiceProvider root, Guid tenantId, string what, Func<ManualFiler, Task<ManualFilingOutcome>> file, ILogger logger)
    {
        try
        {
            using var scope = root.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            await file(scope.ServiceProvider.GetRequiredService<ManualFiler>());
        }
        catch (Exception e)
        {
            // Deliberately broad — the ModuleMaskBackfill posture.
            logger.LogError(e, "Filing the {What} manual failed for tenant {TenantId}", what, tenantId);
        }
    }
}
