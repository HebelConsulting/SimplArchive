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
                    var builder = new DbContextOptionsBuilder();
                    builder.UseNpgsql(ownerConnectionString, npgsql => npgsql
                        .MigrationsHistoryTable(HistoryTable(loaded.Module.ModuleId))
                        .MigrationsAssembly(set.ContextType.Assembly));
                    await using var owned = (DbContext)Activator.CreateInstance(set.ContextType, builder.Options)!;
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
