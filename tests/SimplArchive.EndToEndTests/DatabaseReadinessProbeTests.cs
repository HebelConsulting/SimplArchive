using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The readiness probe sees a database that has started refusing NEW connections — and classifies the refusal
// (#1287, ADR 0807).
//
// WHAT THIS PINS, AND WHY IT NEEDS A REAL POSTGRES. The defect was never in our code's logic; it was in what a
// pooled connection open DOES. A pooled open performs no handshake, so the old check re-presented a physical
// connection authenticated long before and never learned the credential had died. Nothing but a real server
// that can be made to refuse a real login can show that, which is why this lives here and not in the unit
// suite. The mapping from refusal to health status is unit-tested separately and cheaply.
//
// EVERY ROLE HERE IS MADE BY THE TEST. The suite shares one Postgres, so breaking a login it did not create
// would break every other test in the leg — the mutation rule in CLAUDE.md, in the one place it would bite
// hardest. Each role is named for the run and dropped afterwards.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class DatabaseReadinessProbeTests
{
    private readonly E2EApiFactory _factory;

    public DatabaseReadinessProbeTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_rotated_password_is_seen_by_the_probe_and_stays_invisible_to_a_pooled_CanConnect()
    {
        var role = await CreateRoleAsync();
        try
        {
            await using var probe = Probe(role.ConnectionString);

            // A POOLED context, warmed exactly as a long-lived app's is: this stands in for the old health
            // check, and its answers are the negative control for the whole change.
            var pooled = new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(role.ConnectionString).Options;
            await using (var db = new ProbeContext(pooled))
            {
                Assert.True(await db.Database.CanConnectAsync());
            }

            Assert.Equal(DatabaseReachability.Reachable, await probe.CheckAsync(CancellationToken.None));

            await SuperuserAsync($"ALTER ROLE \"{role.Name}\" PASSWORD 'rotated-underneath-the-process';");

            // THE FINDING. The pooled check still says yes — it hands back the physical connection it warmed
            // before the rotation, and a pooled open performs no handshake, so the dead password is never
            // presented. This assertion is expected to keep passing forever: it is not a wish, it is the
            // measured behaviour of the thing the probe had to stop relying on.
            await using (var db = new ProbeContext(pooled))
            {
                Assert.True(await db.Database.CanConnectAsync());
            }

            // THE FIX. A fresh physical connection presents the credential and is told no.
            var refused = await probe.CheckAsync(CancellationToken.None);
            Assert.Equal(DatabaseReachabilityState.CredentialRefused, refused.State);
            Assert.Equal("28P01", refused.SqlState);
        }
        finally
        {
            await DropRoleAsync(role.Name);
        }
    }

    [Fact]
    public async Task A_connection_limit_is_degraded_rather_than_a_refused_credential()
    {
        // CONNECTION LIMIT 0 rather than holding a connection open against a limit of 1: the refusal is then
        // deterministic and instant, with no second session to race. Measured to produce the same 53300 the
        // server-wide max_connections does.
        var role = await CreateRoleAsync(connectionLimit: 0);
        try
        {
            await using var probe = Probe(role.ConnectionString);

            var saturated = await probe.CheckAsync(CancellationToken.None);

            // The whole reason this class classifies instead of returning a boolean. Answering "unhealthy"
            // here would take every replica out of rotation at the same instant, while their pooled
            // connections were still serving — a shortage turned into an outage.
            Assert.Equal(DatabaseReachabilityState.Saturated, saturated.State);
            Assert.Equal("53300", saturated.SqlState);
        }
        finally
        {
            await DropRoleAsync(role.Name);
        }
    }

    [Fact]
    public async Task A_server_that_never_answers_reports_unreachable_with_no_sqlstate()
    {
        // Port 1 is reserved and nothing listens on it. The distinction being pinned is that there is NO
        // SQLSTATE: the server did not refuse us, it never spoke — so the response body cannot show the
        // server's verdict, because there isn't one.
        await using var probe = Probe("Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password=none");

        var unreachable = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal(DatabaseReachabilityState.Unreachable, unreachable.State);
        Assert.Null(unreachable.SqlState);
    }

    private static NpgsqlDatabaseReachabilityProbe Probe(string connectionString) =>
        new(connectionString,
            // No secrets store, which is every test and every non-OpenBao deployment: the connection string
            // carries its own password and the probe leaves it alone.
            new RefreshableDatabasePassword(provider: null, NullLogger.Instance),
            NullLogger.Instance);

    private async Task<(string Name, string ConnectionString)> CreateRoleAsync(int? connectionLimit = null)
    {
        var name = $"probe_{Guid.NewGuid():N}";
        const string password = "probepw1234";
        var limit = connectionLimit is { } value ? $" CONNECTION LIMIT {value}" : string.Empty;

        await SuperuserAsync($"CREATE ROLE \"{name}\" LOGIN PASSWORD '{password}'{limit};");

        var builder = new NpgsqlConnectionStringBuilder(_factory.PostgresSuperuserConnectionString)
        {
            Username = name,
            Password = password,
        };
        return (name, builder.ConnectionString);
    }

    private async Task DropRoleAsync(string name)
    {
        // The role's own connections are gone (the probe never pools and each context is disposed), so a plain
        // DROP is enough. Left in a finally so a failing assertion does not leak a role into the shared server.
        await SuperuserAsync($"DROP ROLE IF EXISTS \"{name}\";");
    }

    private async Task SuperuserAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_factory.PostgresSuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // A DbContext with no model at all — CanConnectAsync only opens a connection, so this is exactly what the
    // health check used to do, with nothing else in the way.
    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options);
}
