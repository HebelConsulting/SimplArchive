using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Tells each loaded module that a document it projects has changed (ABI 1.1, ADR 0848).
/// </summary>
/// <remarks>
/// <para>
/// A SINGLETON holding only the declarations — which module wants which mask, assembled once from the loaded
/// assemblies because that is process-global. Everything per-request (the facade, the module's own read-model
/// context) is resolved from the scope at dispatch time through the injected provider.
/// </para>
/// <para>
/// <b>The activation gate applies here like everywhere else.</b> A module whose licence has lapsed does not
/// project — its hook is not called, its rows go stale, and a rebuild after re-activation is the remedy. That
/// is the correct direction: ADR 0740's promise is that the DATA is the tenant's and the BEHAVIOUR is licensed,
/// and projecting is behaviour. It also means the delivery tier stops answering at the same moment for the same
/// reason, rather than answering from a projection nobody is maintaining.
/// </para>
/// </remarks>
public sealed class ModuleProjectionDispatcher : IModuleProjectionDispatcher
{
    private readonly IReadOnlyList<(string ModuleId, IIndustryModule Module, HashSet<Guid> Masks)> _declared;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;

    public ModuleProjectionDispatcher(
        IReadOnlyList<ModuleLoader.LoadedModule> modules, IServiceScopeFactory scopes, TimeProvider? clock = null)
    {
        _scopes = scopes;
        _clock = clock ?? TimeProvider.System;

        var declared = new List<(string, IIndustryModule, HashSet<Guid>)>();
        var masks = new HashSet<Guid>();
        foreach (var loaded in modules)
        {
            // Both halves are required: a module that names masks but supplies no hook has declared an
            // intention it cannot act on, and one with a hook and no masks is never asked. Neither is an
            // error worth refusing a load over — it is simply not a projector.
            if (loaded.Module.DocumentProjected is null || loaded.Module.ProjectedMasks.Count == 0)
            {
                continue;
            }

            var own = loaded.Module.ProjectedMasks.ToHashSet();
            declared.Add((loaded.Module.ModuleId, loaded.Module, own));
            masks.UnionWith(own);
        }

        _declared = declared;
        ProjectedMasks = masks;
    }

    public IReadOnlyCollection<Guid> ProjectedMasks { get; }

    public async Task DispatchAsync(
        Guid documentId, Guid maskId, Guid tenantId, bool removed, CancellationToken cancellationToken = default)
    {
        var interested = _declared.Where(d => d.Masks.Contains(maskId)).ToList();
        if (interested.Count == 0)
        {
            return;
        }

        // A scope of its own, deliberately. The caller is inside SimplArchiveDbContext.SaveChangesAsync, so
        // resolving the module's read-model context from the AMBIENT scope would hand it a context whose
        // ChangeTracker is mid-save — and a module calling SaveChanges on it would re-enter the very method
        // that is running. A fresh scope shares the connection and the transaction (the host wires module
        // contexts onto the core's connection, ADR 0738) while keeping the trackers apart.
        using var scope = _scopes.CreateScope();
        var facade = scope.ServiceProvider.GetRequiredService<IModuleArchiveFacade>();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.SimplArchiveDbContext>();

        foreach (var (moduleId, module, _) in interested)
        {
            if (!await ModuleActivationCheck.IsActiveAsync(db, moduleId, _clock.GetUtcNow(), cancellationToken))
            {
                continue;
            }

            // NOT wrapped in a try/catch, and that is the decision rather than an omission (ADR 0848). Every
            // other module hook is tolerated because its failure refuses something — safe and visible. A
            // projection that silently did not happen leaves a revoked certificate addressed until somebody
            // thinks to rebuild, so this one fails the write that triggered it.
            await module.DocumentProjected!(new ProjectedDocumentContext(
                documentId, maskId, tenantId, removed, facade, scope.ServiceProvider));
        }
    }
}
