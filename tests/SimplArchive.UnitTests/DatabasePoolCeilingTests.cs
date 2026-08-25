using Npgsql;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.UnitTests;

// The pool ceiling that keeps the app's connection pool smaller than the database (#750).
//
// Written because the defect it prevents was invisible for the life of the project: nothing failed, nothing
// warned, and the two numbers that had to be related — Npgsql's default 100 and Postgres's max_connections —
// were set in different places by different people. It surfaced only under sustained load, as 500s.
public class DatabasePoolCeilingTests
{
    private const string Base = "Host=db;Database=simplarchive;Username=app;Password=secret";

    [Fact]
    public void An_unspecified_pool_is_capped_rather_than_left_at_Npgsqls_hundred()
    {
        // THE defect, in one assertion. Npgsql's default is 100 per process; the kiosk's database could serve
        // ~93 in total, so the ceiling exceeded capacity before anyone deployed anything.
        var (connectionString, size, source) = DatabasePoolCeiling.Apply(Base, configured: null);

        Assert.Equal(DatabasePoolCeiling.DefaultMaxPoolSize, size);
        Assert.Equal(PoolCeilingSource.Default, source);
        Assert.Equal(DatabasePoolCeiling.DefaultMaxPoolSize, new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize);
        Assert.True(size < 100, "the whole point is to be below Npgsql's default");
    }

    [Fact]
    public void The_default_leaves_the_charts_two_replicas_inside_the_smallest_database()
    {
        // The default is only defensible if the SHIPPED configuration fits: the chart runs 2 replicas, and the
        // smallest database this is deployed against (db.t4g.micro, ~112 max_connections) must still have room
        // for the migration Job and OpenBao's own connections.
        const int chartDefaultReplicas = 2;
        const int smallestUsableSlots = 112 - 3; // max_connections − superuser_reserved

        var total = DatabasePoolCeiling.DefaultMaxPoolSize * chartDefaultReplicas;

        Assert.True(total < smallestUsableSlots,
            $"{chartDefaultReplicas} replicas × {DatabasePoolCeiling.DefaultMaxPoolSize} = {total} must fit inside {smallestUsableSlots}");
    }

    [Fact]
    public void A_configured_size_is_used_because_the_installer_has_done_the_arithmetic()
    {
        // A deployment that scales cannot use the default: the installer knows the tier, the instance class and
        // the replica count, so its number wins over ours.
        var (connectionString, size, source) = DatabasePoolCeiling.Apply(Base, configured: 30);

        Assert.Equal(30, size);
        Assert.Equal(PoolCeilingSource.Configuration, source);
        Assert.Equal(30, new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize);
    }

    [Fact]
    public void An_explicit_keyword_in_the_connection_string_beats_both()
    {
        // The operator being specific about their own database — the knowledge this code does not have. Silently
        // overriding it would be the same mistake in the other direction.
        var (connectionString, size, source) = DatabasePoolCeiling.Apply(
            $"{Base};Maximum Pool Size=250", configured: 30);

        Assert.Equal(250, size);
        Assert.Equal(PoolCeilingSource.ConnectionString, source);
        Assert.Equal(250, new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize);
    }

    [Fact]
    public void The_rest_of_the_connection_string_survives_untouched()
    {
        // It is rebuilt through a builder, and it carries the credential OpenBao just issued — losing a keyword
        // here would break startup in a way that looks like a secrets problem.
        var (connectionString, _, _) = DatabasePoolCeiling.Apply(
            $"{Base};Include Error Detail=true", configured: 25);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.Equal("db", builder.Host);
        Assert.Equal("simplarchive", builder.Database);
        Assert.Equal("app", builder.Username);
        Assert.Equal("secret", builder.Password);
        Assert.True(builder.IncludeErrorDetail);
    }

    [Fact]
    public void A_nonsense_size_is_refused_rather_than_quietly_applied()
    {
        // A zero pool would hang every request forever, which reads as the database being down.
        Assert.Throws<ArgumentOutOfRangeException>(() => DatabasePoolCeiling.Apply(Base, configured: 0));
    }
}
