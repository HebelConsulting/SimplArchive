using Npgsql;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Caps the Npgsql connection pool so it cannot be larger than the database it connects to (issue #750).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Npgsql's default <c>Maximum Pool Size</c> is <b>100</b>, chosen with no knowledge of
/// the server, while a PostgreSQL instance has a hard <c>max_connections</c> (100 on the kiosk, ~112 on a 1 GiB
/// RDS instance) of which <c>superuser_reserved_connections</c> is unavailable to us. Nothing related the two,
/// so the pool's ceiling exceeded the server's capacity <b>by construction</b> — and the first real load run
/// (#705) crossed it: Postgres answered <c>53300: remaining connection slots are reserved…</c> and the Api
/// returned <b>500</b>s for eight minutes.
/// </para>
/// <para>
/// <b>Capping does more than avoid the error — it changes the failure mode.</b> Below the server's limit,
/// Npgsql <i>queues</i> a caller until a connector frees up instead of opening one Postgres will refuse. The
/// same overload then presents as <i>slow</i> rather than <i>broken</i>, which is both the correct behaviour at
/// saturation and the only version a load test can measure.
/// </para>
/// <para>
/// <b>The ceiling is per PROCESS, and that is the trap.</b> Every replica carries its own pool, so the budget
/// that matters is the sum across replicas plus everything else connecting:
/// </para>
/// <code>
/// pool × maxReplicas + migration job + OpenBao  &lt;  max_connections − superuser_reserved
/// </code>
/// <para>
/// A deployment that scales cannot therefore rely on the default here: the installer computes the value from
/// the tier's instance class and replica count, and <c>preflight.sh</c> refuses a combination that cannot fit.
/// This class only guarantees that <i>something</i> sane applies when nobody has computed anything.
/// </para>
/// </remarks>
public static class DatabasePoolCeiling
{
    /// <summary>
    /// The pool size used when neither the connection string nor configuration specifies one.
    /// </summary>
    /// <remarks>
    /// Deliberately far below Npgsql's 100. A web workload's concurrent <i>query</i> count is much smaller than
    /// its concurrent request count — requests spend most of their life outside the database — so 40 is generous
    /// for one process, while leaving the chart's default two replicas (80) inside the smallest database this
    /// project is deployed against (~112 usable) with room for the migration Job and OpenBao's own connections.
    /// It is a safe default, NOT a recommendation: a deployment that scales must compute its own.
    /// </remarks>
    public const int DefaultMaxPoolSize = 40;

    /// <summary>The connection-string keyword an operator can set to override everything here.</summary>
    private const string MaxPoolSizeKeyword = "Maximum Pool Size";

    /// <summary>
    /// Returns <paramref name="connectionString"/> with a pool ceiling applied, and reports what happened.
    /// </summary>
    /// <param name="connectionString">The connection string as configured (or as built from OpenBao).</param>
    /// <param name="configured">
    /// <c>Database:MaxPoolSize</c>, when set. This is what a chart/installer supplies once it has done the
    /// arithmetic above.
    /// </param>
    /// <returns>
    /// The effective connection string and the pool size it carries, so the caller can log the number — an
    /// operator diagnosing saturation needs to know what the ceiling actually was, and it is otherwise invisible.
    /// </returns>
    public static (string ConnectionString, int MaxPoolSize, PoolCeilingSource Source) Apply(
        string connectionString, int? configured)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // An explicit keyword in the connection string WINS over configuration and over the default. It is the
        // operator being specific about their own database, which is exactly the knowledge this class lacks.
        //
        // Asked of the UNTYPED builder on purpose: NpgsqlConnectionStringBuilder.ContainsKey answers true for a
        // keyword that merely has its DEFAULT value, so it cannot distinguish "the operator said 100" from
        // "nobody said anything and Npgsql's default is 100" — which is the only question here. The untyped
        // DbConnectionStringBuilder holds exactly the keywords the string carried, and matches case-insensitively.
        var written = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        if (written.ContainsKey(MaxPoolSizeKeyword) || written.ContainsKey("MaxPoolSize"))
        {
            return (connectionString, builder.MaxPoolSize, PoolCeilingSource.ConnectionString);
        }

        if (configured is { } explicitSize)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(explicitSize, 1);
            builder.MaxPoolSize = explicitSize;
            return (builder.ConnectionString, explicitSize, PoolCeilingSource.Configuration);
        }

        builder.MaxPoolSize = DefaultMaxPoolSize;
        return (builder.ConnectionString, DefaultMaxPoolSize, PoolCeilingSource.Default);
    }
}

/// <summary>
/// The pool ceiling this process ended up with, so startup can state it.
/// </summary>
/// <remarks>
/// An operator diagnosing connection saturation needs the effective number, and it is otherwise invisible —
/// it lives inside a connection string they must not print, since that string carries the password.
/// </remarks>
public sealed record DatabasePoolInfo(int MaxPoolSize, PoolCeilingSource Source);

/// <summary>Where the effective pool ceiling came from — worth logging, since only one of these was chosen.</summary>
public enum PoolCeilingSource
{
    /// <summary>Nobody said, so <see cref="DatabasePoolCeiling.DefaultMaxPoolSize"/> applies.</summary>
    Default,

    /// <summary><c>Database:MaxPoolSize</c> — normally computed by the installer from tier and replica count.</summary>
    Configuration,

    /// <summary>An explicit <c>Maximum Pool Size</c> keyword; the operator overrode us on purpose.</summary>
    ConnectionString,
}
