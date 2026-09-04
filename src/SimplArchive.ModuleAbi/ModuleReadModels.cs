using Microsoft.EntityFrameworkCore;

namespace SimplArchive.ModuleAbi;

/// <summary>
/// The base every module read-model context derives from (ADR 0738): a DbContext of the module's own,
/// with its own migrations and its own history table, on the HOST's database and connection.
/// </summary>
/// <remarks>
/// <para>
/// The host constructs it — provider, connection and the per-module migrations-history table are wired
/// there, which is why a module never holds a connection string (credential rotation stays invisible,
/// ADR 0721) and why a transition handler's projection writes land in the SAME transaction as its
/// document writes (the engine enlists every registered module context, ADR 0737).
/// </para>
/// <para>
/// Model rules (ADR 0738): Fluent API only, provider-agnostic (PostgreSQL in production, SQLite in the
/// module's own tests — the host applies real migrations on the first and <c>EnsureCreated</c> on the
/// second); every table name carries the module's prefix (<c>fs_*</c>), so an operator reading the schema
/// can attribute every table and collisions are impossible by construction; and DERIVED data only — a
/// read model is rebuildable from documents by contract (<see cref="IModuleProjectionRebuilder"/>), and
/// nothing in one may be the only copy of anything. A fact that cannot be rebuilt is a document.
/// </para>
/// </remarks>
public abstract class ModuleDbContext : DbContext
{
    /// <summary>Constructed by the host with the wired options; module code only ever injects it.</summary>
    protected ModuleDbContext(DbContextOptions options)
        : base(options)
    {
    }
}

/// <summary>One read-model context a module declares (ADR 0738), for the host to wire and migrate.</summary>
/// <param name="ContextType">The module's <see cref="ModuleDbContext"/> subclass.</param>
public sealed record ModuleReadModelSet(Type ContextType);

/// <summary>
/// The rebuild contract (ADR 0738): every projection must be re-derivable from documents, and this is the
/// command that does it — the operator's guarantee that backup/DR of documents is backup/DR of
/// everything, and the support case's first answer. Registered in the module's
/// <see cref="IIndustryModule.ConfigureServices"/> (scoped), invoked by the host's admin endpoint for the
/// ambient tenant.
/// </summary>
public interface IModuleProjectionRebuilder
{
    /// <summary>The projections this rebuilder answers for, by stable name.</summary>
    IReadOnlyList<string> ProjectionNames { get; }

    /// <summary>Re-derives one projection for the ambient tenant, replacing whatever the tables held.</summary>
    Task RebuildAsync(string projectionName, CancellationToken cancellationToken = default);
}
