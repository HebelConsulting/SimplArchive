using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<ModuleProjectionDispatcher>? _logger;

    public ModuleProjectionDispatcher(
        IReadOnlyList<ModuleLoader.LoadedModule> modules,
        IServiceScopeFactory scopes,
        TimeProvider? clock = null,
        ILogger<ModuleProjectionDispatcher>? logger = null)
    {
        _scopes = scopes;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;

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
        Guid documentId,
        Guid maskId,
        Guid tenantId,
        bool removed,
        AmbientDatabaseTransaction? ambient = null,
        CancellationToken cancellationToken = default)
    {
        var interested = _declared.Where(d => d.Masks.Contains(maskId)).ToList();
        if (interested.Count == 0)
        {
            return;
        }

        // A scope of its own, deliberately. The caller is inside SimplArchiveDbContext.SaveChangesAsync, so
        // resolving the module's read-model context from the AMBIENT scope would hand it a context whose
        // ChangeTracker holds that save's entities — and a module calling SaveChanges on it would re-enter the
        // very method that is running. A fresh scope keeps the trackers apart.
        //
        // What it does NOT get for free is the save's connection and transaction, which is the whole subject
        // of the two blocks below. The comment that used to stand here said it shared them; it did not, and
        // nothing failed loudly enough to correct it.
        using var scope = _scopes.CreateScope();

        // THE FRESH SCOPE HAS NO AMBIENT TENANT, and everything below needs one (#1495). The accessor is
        // scoped, so a new scope means a new one with TenantId null — and the tenant query filter's
        // `TenantId == null` predicate then matches NOTHING, silently. Two things broke on that: the
        // activation check below decided the module was inactive and skipped the projection with a bare
        // `continue`, and the module's own hook would have read an empty archive through the facade.
        //
        // The tenant is a PARAMETER of this method and was simply not being used. Measured on the demo
        // stack: a certificate enrolled through the encryption module's own surface never reached its read
        // model, so the module answered "this reader holds none" — and because a module that answers is the
        // ONLY source (ADR 0842), activating the module REMOVED the reader's access. A rebuild fixed it
        // every time, which is the tell: the rebuild runs in a REQUEST scope, where the tenant is set.
        ((Persistence.CurrentTenantAccessor)scope.ServiceProvider
            .GetRequiredService<Application.Abstractions.ICurrentTenantAccessor>()).TenantId = tenantId;

        var db = scope.ServiceProvider.GetRequiredService<Persistence.SimplArchiveDbContext>();

        // AND IT HAS NO SIGHT OF THE SAVE EITHER, unless it is lent the connection the save is running on
        // (#1495). A fresh scope means a fresh context, which takes its own connection from the pool — and the
        // rows this save wrote are not committed yet, so on that connection they are not there. The hook asked
        // the facade for the document it had just been called about, was told there is no such document, and
        // took its own "nothing to derive" branch: no row, no throw, no log. See
        // AmbientDatabaseTransaction for the measurement.
        //
        // Set BEFORE anything else in this scope touches the database, and before any module read-model
        // context is resolved: those take their connection from this very context when they are built
        // (ADR 0738), so they inherit the lent one for free.
        var enlisted = new List<DbContext>();
        if (ambient is not null)
        {
            db.Database.SetDbConnection(ambient.Connection, contextOwnsConnection: false);
            await db.Database.UseTransactionAsync(ambient.Transaction, cancellationToken);
            enlisted.Add(db);
        }

        var facade = scope.ServiceProvider.GetRequiredService<IModuleArchiveFacade>();

        // Every module read-model context joins the same transaction, exactly as the state-machine engine
        // enlists them (ADR 0737/0738). Without this a module's projection row commits on its own, so the
        // projection could survive a document write that rolled back — the case ADR 0848's single transaction
        // exists to rule out, and which this arrangement was quietly not delivering.
        if (ambient is not null)
        {
            foreach (var contextType in
                scope.ServiceProvider.GetService<ModuleReadModelCatalog>()?.ContextTypes ?? [])
            {
                var readModel = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
                await readModel.Database.UseTransactionAsync(ambient.Transaction, cancellationToken);
                enlisted.Add(readModel);
            }
        }

        try
        {
            foreach (var (moduleId, module, _) in interested)
            {
                if (!await ModuleActivationCheck.IsActiveAsync(db, moduleId, _clock.GetUtcNow(), cancellationToken))
                {
                    // A SKIP THAT SAYS SO. ADR 0848's own argument is that a projection which silently did not
                    // happen is discovered by nobody, and this was the one branch that did exactly that — a
                    // stale read model, and nothing anywhere distinguishing it from one that was re-derived.
                    // Warning rather than Debug because it needs an administrator: the remedy is to re-activate
                    // and rebuild, and until then the module's answers are out of date.
                    _logger?.LogWarning(
                        "Module {ModuleId} declares mask {MaskId} but is not active for tenant {TenantId}, so "
                        + "document {DocumentId} was NOT re-projected. Its read model is now stale — rebuild "
                        + "the projection after re-activating the module.",
                        moduleId, maskId, tenantId, documentId);
                    continue;
                }

                // NOT wrapped in a try/catch, and that is the decision rather than an omission (ADR 0848). Every
                // other module hook is tolerated because its failure refuses something — safe and visible. A
                // projection that silently did not happen leaves a revoked certificate addressed until somebody
                // thinks to rebuild, so this one fails the write that triggered it.
                await module.DocumentProjected!(new ProjectedDocumentContext(
                    documentId, maskId, tenantId, removed, facade, scope.ServiceProvider));

                _logger?.LogDebug(
                    "Module {ModuleId} re-projected document {DocumentId} (mask {MaskId}, removed {Removed}).",
                    moduleId, documentId, maskId, removed);
            }
        }
        finally
        {
            // Un-enlisted before the scope goes, so disposing these contexts cannot touch a transaction the
            // CALLER still owns and is about to commit.
            for (var i = enlisted.Count - 1; i >= 0; i--)
            {
                await enlisted[i].Database.UseTransactionAsync(null, CancellationToken.None);
            }
        }
    }
}
