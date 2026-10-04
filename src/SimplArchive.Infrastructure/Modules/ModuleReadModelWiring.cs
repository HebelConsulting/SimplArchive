using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>The module read-model context types the host wired — what the engine enlists (ADR 0738).</summary>
public sealed record ModuleReadModelCatalog(IReadOnlyList<Type> ContextTypes)
{
    /// <summary>No modules, no contexts — the engine's default when nothing registers one.</summary>
    public static readonly ModuleReadModelCatalog Empty = new([]);
}

/// <summary>
/// Wires every declared module read-model context (ADR 0738): registered in DI on the CORE context's own
/// connection (a module never sees a connection string — rotation stays invisible, and sharing the
/// connection is what lets the engine's transaction cover documents AND projections), with the module's
/// own migrations assembly and its own <c>__EFMigrationsHistory_&lt;module&gt;</c> table.
/// </summary>
public static class ModuleReadModelWiring
{
    private static readonly MethodInfo AddDbContextMethod = typeof(EntityFrameworkServiceCollectionExtensions)
        .GetMethods()
        .Single(m => m.Name == nameof(EntityFrameworkServiceCollectionExtensions.AddDbContext)
            && m.GetGenericArguments().Length == 1
            && m.GetParameters() is { Length: 4 } p
            && p[1].ParameterType == typeof(Action<IServiceProvider, DbContextOptionsBuilder>));

    /// <summary>DI registration for every module context; returns the catalog the engine enlists.</summary>
    public static ModuleReadModelCatalog AddModuleReadModels(
        this IServiceCollection services, IReadOnlyList<ModuleLoader.LoadedModule> modules)
    {
        var contextTypes = new List<Type>();
        foreach (var loaded in modules)
        {
            foreach (var set in loaded.Module.ReadModels)
            {
                var moduleId = loaded.Module.ModuleId;
                var contextType = set.ContextType;
                contextTypes.Add(contextType);

                // THE CONTEXT IS CONSTRUCTED WITH ITS OWN TYPED OPTIONS, explicitly — registered BEFORE AddDbContext,
                // which only TryAdds the context type, so this descriptor is the one that resolves.
                //
                // Without it, a module context written with the documented non-generic constructor
                // (`XContext(DbContextOptions options)`) is bound by DI to the non-generic `DbContextOptions`
                // service — which every AddDbContext call ADDS, so with two modules loaded the last one registered
                // wins and every other module's context is handed the wrong options. EF then throws ("must be a
                // DbContextOptions<XContext>"). Measured on the first host to load two modules (the kiosk on
                // v0.37.0, flight school + encryption): a flight-school transition answered 500 because module
                // dispatch constructed the encryption read model. CanHostConstruct guarantees the single parameter
                // a DbContextOptions<TContext> is assignable to, so this serves both documented shapes.
                var typedOptions = typeof(DbContextOptions<>).MakeGenericType(contextType);
                services.Add(new ServiceDescriptor(
                    contextType,
                    sp => Activator.CreateInstance(contextType, sp.GetRequiredService(typedOptions))!,
                    ServiceLifetime.Scoped));

                AddDbContextMethod.MakeGenericMethod(contextType).Invoke(null,
                [
                    services,
                    (Action<IServiceProvider, DbContextOptionsBuilder>)((sp, options) =>
                        Configure(sp, options, moduleId, contextType)),
                    ServiceLifetime.Scoped,
                    ServiceLifetime.Scoped,
                ]);
            }
        }

        var catalog = new ModuleReadModelCatalog(contextTypes);
        services.AddSingleton(catalog);
        return catalog;
    }

    private static void Configure(IServiceProvider services, DbContextOptionsBuilder options, string moduleId, Type contextType)
    {
        // The CORE context's own live connection: one connection, one transaction, one commit — the
        // atomicity ADR 0737 rests on. The provider follows the core's (PostgreSQL in production and the
        // E2E harness, SQLite in the module's own in-memory tests).
        var core = services.GetRequiredService<SimplArchiveDbContext>();
        var connection = core.Database.GetDbConnection();
        if (core.Database.IsNpgsql())
        {
            options.UseNpgsql(connection, npgsql => npgsql
                .MigrationsHistoryTable(HistoryTable(moduleId))
                .MigrationsAssembly(contextType.Assembly));
        }
        else
        {
            options.UseSqlite(connection);
        }
    }

    /// <summary>
    /// The options the host hands a module's context when it constructs one ITSELF, outside DI — the
    /// owner-connection migration path (ADR 0721), which is the only place that happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Built as <c>DbContextOptionsBuilder&lt;TContext&gt;</c>, by reflection, deliberately</b> (#1475). The
    /// non-generic <c>DbContextOptionsBuilder</c> reads as the right choice here — the host does not know the
    /// context type at compile time — but its <c>Options</c> is a <c>DbContextOptions&lt;DbContext&gt;</c>:
    /// the type argument is WRONG rather than absent, so it satisfies a constructor taking
    /// <c>DbContextOptions</c> and misses the <c>DbContextOptions&lt;TContext&gt;</c> that EF documents and
    /// that every tutorial writes. The generic builder satisfies BOTH, because
    /// <c>DbContextOptions&lt;TContext&gt;</c> derives from <c>DbContextOptions</c> and the binder matches on
    /// assignability. So this is not a widening of the contract: it makes this path agree with the DI path,
    /// which has always accepted either (EF registers both).
    /// </para>
    /// <para>
    /// <b>No test suite exercises this path</b>, which is why the trap was invisible: it runs only when
    /// <c>ConnectionStrings:Migration</c> is set — a real deployment or <c>db-migrate</c>. The module's own
    /// suite constructs its context directly, the host's suites go through DI, and the failure arrived as a
    /// <c>MissingMethodException</c> from inside <c>Activator</c> at a customer's migration, reading as a host
    /// defect. <c>ModuleReadModelConstructionTests</c> therefore drives THIS method rather than a copy of it.
    /// </para>
    /// </remarks>
    public static DbContextOptions OwnerOptions(Type contextType, string ownerConnectionString, string moduleId)
    {
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;

        builder.UseNpgsql(ownerConnectionString, npgsql => npgsql
            .MigrationsHistoryTable(HistoryTable(moduleId))
            .MigrationsAssembly(contextType.Assembly));

        return builder.Options;
    }

    /// <summary>
    /// Whether the host can construct this declared context at all — asked at LOAD, so a module that cannot
    /// be migrated says so by name instead of dying inside <c>Activator</c> later (#1475).
    /// </summary>
    /// <remarks>
    /// Mirrors the binder rule <see cref="OwnerOptions"/> relies on: one public constructor parameter, to
    /// which a <c>DbContextOptions&lt;TContext&gt;</c> is assignable. Both documented shapes pass; a
    /// constructor taking extra parameters, a non-public one, or none at all does not — and those are real,
    /// since a module author reaching for an injected dependency writes exactly the first.
    /// </remarks>
    public static bool CanHostConstruct(Type contextType)
    {
        var options = typeof(DbContextOptions<>).MakeGenericType(contextType);

        return contextType.GetConstructors().Any(
            c => c.GetParameters() is [{ } only] && only.ParameterType.IsAssignableFrom(options));
    }

    /// <summary>The per-module migrations-history table — the core's history never learns a module exists.</summary>
    public static string HistoryTable(string moduleId) =>
        $"__EFMigrationsHistory_{moduleId.Replace('-', '_')}";

    /// <summary>
    /// Applies every module context's schema — real migrations on PostgreSQL (through
    /// <paramref name="ownerConnectionString"/> where the deployment separates DDL from runtime, ADR 0721),
    /// <c>CreateTables</c> on SQLite, where the relational creator is the only way to add one context's
    /// tables to a database that already holds the core's (<c>EnsureCreated</c> is all-or-nothing per
    /// database and silently does NOTHING when any table exists — the known trap).
    /// </summary>
    public static async Task MigrateAllAsync(
        IServiceProvider scopedServices,
        IReadOnlyList<ModuleLoader.LoadedModule> modules,
        string? ownerConnectionString,
        CancellationToken cancellationToken = default)
    {
        foreach (var loaded in modules)
        {
            foreach (var set in loaded.Module.ReadModels)
            {
                if (!string.IsNullOrWhiteSpace(ownerConnectionString))
                {
                    var options = OwnerOptions(set.ContextType, ownerConnectionString, loaded.Module.ModuleId);
                    await using var owned = (DbContext)Activator.CreateInstance(set.ContextType, options)!;
                    await owned.Database.MigrateAsync(cancellationToken);
                    continue;
                }

                var context = (DbContext)scopedServices.GetRequiredService(set.ContextType);
                if (context.Database.IsNpgsql())
                {
                    await context.Database.MigrateAsync(cancellationToken);
                }
                else
                {
                    try
                    {
                        context.GetService<IRelationalDatabaseCreator>().CreateTables();
                    }
                    catch (Exception)
                    {
                        // The tables exist — CreateTables has no "if missing" mode, so the second run
                        // throwing IS the idempotence signal.
                    }
                }
            }
        }
    }
}
