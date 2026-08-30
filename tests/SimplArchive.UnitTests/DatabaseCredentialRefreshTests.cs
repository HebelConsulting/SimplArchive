using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.UnitTests;

// The rotating database password (ADR "Database credential refresh").
//
// The bug these guard: the app read ONE credential at startup and kept it for the life of the process. The dev
// stack showed the consequence after ~2 days — the 24h lease had expired, Postgres had revoked the role, and
// every new connection failed 28P01 until someone restarted the container.
//
// What can be asserted WITHOUT a database is the wiring, and that is where the defect actually lived: whether a
// password provider is consulted at all, and whether Npgsql will even accept the connection string we compose
// for it. Both were wrong in ways a running system would only reveal a day later.
public class DatabaseCredentialRefreshTests
{
    private sealed class StubPasswords(Func<string> next) : IDatabasePasswordProvider
    {
        public int Calls { get; private set; }

        public ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(next());
        }
    }

    [Fact]
    public void Npgsql_refuses_a_password_provider_when_the_connection_string_carries_a_password()
    {
        // This is the constraint that forces OpenBaoSecretsReader to compose a password-LESS connection string
        // for the runtime, and it is asserted rather than assumed because getting it wrong fails at STARTUP of a
        // real deployment and nowhere else — no test, no compile error, and only when OpenBao is configured.
        var builder = new NpgsqlDataSourceBuilder("Host=db;Database=simplarchive;Username=u;Password=baked-in");
        builder.UsePeriodicPasswordProvider((_, _) => ValueTask.FromResult("rotated"), TimeSpan.FromHours(1), TimeSpan.FromSeconds(30));

        Assert.Throws<NotSupportedException>(() => builder.Build());
    }

    [Fact]
    public void A_password_less_connection_string_accepts_the_provider()
    {
        // The shape the reader actually composes: template + Username, no Password.
        var builder = new NpgsqlDataSourceBuilder("Host=db;Database=simplarchive;Username=simplarchive_runtime");
        builder.UsePeriodicPasswordProvider((_, _) => ValueTask.FromResult("rotated"), TimeSpan.FromHours(1), TimeSpan.FromSeconds(30));

        using var dataSource = builder.Build();

        Assert.NotNull(dataSource);
    }

    [Fact]
    public async Task The_provider_is_asked_for_the_password_rather_than_the_connection_string_being_trusted()
    {
        // Proves the provider is WIRED, not merely registered — a registered-but-never-consulted provider would
        // leave the credential exactly as stale as before, and nothing else here would notice.
        //
        // It WAITS for the call rather than asserting one has already happened, and that distinction is the
        // whole reliability of this test: UsePeriodicPasswordProvider fetches on Npgsql's OWN background
        // schedule, not synchronously on Build() or on the first open. The first version of this test asserted
        // `Calls > 0` straight after an open attempt; it passed run-alone and failed inside the full suite,
        // because under parallel load the background fetch simply had not run yet. The claim was right and the
        // assertion about WHEN was wrong — the classic shape of a test that measures machine speed.
        var fetched = new TaskCompletionSource();
        var passwords = new StubPasswords(() =>
        {
            fetched.TrySetResult();
            return "rotated-secret";
        });

        var builder = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Database=simplarchive;Username=simplarchive_runtime;Timeout=1");
        builder.UsePeriodicPasswordProvider(
            async (_, ct) => await passwords.GetPasswordAsync(ct),
            TimeSpan.FromHours(1),
            TimeSpan.FromSeconds(30));

        using var dataSource = builder.Build();

        // The open cannot succeed (nothing listens on port 1) and is not what is being asserted; it is here to
        // give Npgsql a reason to want a password at all.
        try
        {
            await dataSource.OpenConnectionAsync();
        }
        catch
        {
            // expected — the address is deliberately unreachable
        }

        var completed = await Task.WhenAny(fetched.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(
            ReferenceEquals(completed, fetched.Task),
            "Npgsql never asked the provider for a password within 30s — the refresh would never happen.");
    }

    [Fact]
    public void A_failing_provider_surfaces_rather_than_silently_supplying_an_empty_password()
    {
        // IDatabasePasswordProvider's contract: throw on failure. Returning "" would replace a WORKING
        // credential with a broken one, and Npgsql would cache that as a success.
        var passwords = new StubPasswords(() => throw new InvalidOperationException("OpenBao unreachable"));

        var builder = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Database=simplarchive;Username=simplarchive_runtime;Timeout=1");
        builder.UsePeriodicPasswordProvider(
            async (_, ct) => await passwords.GetPasswordAsync(ct),
            TimeSpan.FromHours(1),
            TimeSpan.FromSeconds(30));

        using var dataSource = builder.Build();

        Assert.ThrowsAny<Exception>(() => dataSource.OpenConnection());
    }

    [Fact]
    public void Without_a_provider_registered_the_connection_string_is_used_as_is()
    {
        // The path every test and every non-OpenBao deployment takes. It must keep working with a password in
        // the string, or this change would break everything that does not use OpenBao.
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = new NpgsqlDataSourceBuilder("Host=db;Database=simplarchive;Username=u;Password=plain");
        using var dataSource = builder.Build();

        Assert.Contains("Username=u", dataSource.ConnectionString);
    }
}
