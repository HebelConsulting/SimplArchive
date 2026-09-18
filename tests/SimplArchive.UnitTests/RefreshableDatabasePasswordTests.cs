using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.UnitTests;

// The runtime database password refreshes ON DEMAND, not on a timer (#1274).
//
// WHAT WAS WRONG. Npgsql's periodic provider refreshes on a schedule, and a schedule does not run while the
// host is suspended. A rotation during a sleep left Npgsql holding a dead password, and every connection failed
// `28P01` until the timer next fired or somebody restarted the process — observed on the dev stack after a
// hibernation, with the container still reporting healthy.
//
// WHAT THESE TESTS ARE REALLY FOR. The fix's risk is not the staleness; it is everything it could break on the
// way past. Asking the secrets store per connection would turn an OpenBao blip into an app outage, and a
// stampede after invalidation would put the store under load exactly when it is least wanted. So the cases
// below are mostly about the BEHAVIOUR PRESERVED, not the behaviour added.
public class RefreshableDatabasePasswordTests
{
    // A local clock rather than Microsoft.Extensions.TimeProvider.Testing: adding a package to the dependency
    // tree — and to the licence gate — for eight lines of fake is a poor trade.
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Fake : IDatabasePasswordProvider
    {
        private readonly Func<int, string> _answer;

        public Fake(Func<int, string> answer) => _answer = answer;

        public int Calls { get; private set; }

        public ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(_answer(Calls));
        }
    }

    private static RefreshableDatabasePassword Build(IDatabasePasswordProvider provider, FakeTimeProvider time) =>
        new(provider, NullLogger.Instance, time);

    [Fact]
    public async Task The_steady_state_is_a_memory_read()
    {
        // The property that makes a per-connection provider affordable at all: a connection open must not cost
        // an HTTP round trip, or the secrets store becomes a dependency of every pool expansion.
        var time = new FakeTimeProvider();
        var provider = new Fake(_ => "p1");
        var password = Build(provider, time);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal("p1", await password.GetAsync(default));
        }

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task It_re_reads_once_the_cache_has_aged_out()
    {
        var time = new FakeTimeProvider();
        var provider = new Fake(calls => $"p{calls}");
        var password = Build(provider, time);

        Assert.Equal("p1", await password.GetAsync(default));
        time.Advance(RefreshableDatabasePassword.Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal("p2", await password.GetAsync(default));
    }

    [Fact]
    public async Task A_rejected_password_is_re_read_immediately_rather_than_at_the_next_tick()
    {
        // THE POINT OF THE CHANGE. The database is the authority on whether a credential is current, and 28P01
        // is it saying so — a signal no timer can have and no health check can infer.
        var time = new FakeTimeProvider();
        var provider = new Fake(calls => $"p{calls}");
        var password = Build(provider, time);

        Assert.Equal("p1", await password.GetAsync(default));

        password.Invalidate();

        // No time has passed at all: the cache is young, and would have kept serving the dead password for the
        // rest of its TTL — which is the outage, in miniature.
        Assert.Equal("p2", await password.GetAsync(default));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task A_secrets_store_outage_keeps_the_app_serving()
    {
        // THE REGRESSION THIS FIX COULD HAVE CAUSED. Before, Npgsql kept the password it held and retried; a
        // brief OpenBao blip was invisible. A per-connection provider that simply threw would have converted
        // that into refused connections — strictly worse than the bug being fixed.
        var time = new FakeTimeProvider();
        var calls = 0;
        var provider = new ThrowingAfterFirst(() => calls++);
        var password = Build(provider, time);

        Assert.Equal("p1", await password.GetAsync(default));
        time.Advance(RefreshableDatabasePassword.Ttl + TimeSpan.FromSeconds(1));

        // The refresh fails, and the credential we hold still works as far as anyone knows.
        Assert.Equal("p1", await password.GetAsync(default));
    }

    [Fact]
    public async Task A_known_bad_password_is_NOT_served_again_when_the_refresh_fails()
    {
        // The exception to keep-old-on-failure, and the half a careless implementation gets wrong. Once the
        // database has refused this password, handing it back is not resilience — it is repeating a failure
        // with the cause hidden, and the caller would see an auth error it cannot explain.
        var time = new FakeTimeProvider();
        var provider = new ThrowingAfterFirst(() => { });
        var password = Build(provider, time);

        Assert.Equal("p1", await password.GetAsync(default));
        password.Invalidate();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await password.GetAsync(default));
    }

    [Fact]
    public async Task A_burst_of_connections_causes_ONE_refresh()
    {
        // Invalidation is followed by a burst of opens. Without single-flight each one calls the store, which
        // is a stampede on the dependency we went to this trouble to stop leaning on.
        var time = new FakeTimeProvider();
        var provider = new SlowFake();
        var password = Build(provider, time);

        var opens = Enumerable.Range(0, 32).Select(_ => password.GetAsync(default).AsTask()).ToArray();
        provider.Release();
        var results = await Task.WhenAll(opens);

        Assert.All(results, r => Assert.Equal("p1", r));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void With_no_provider_it_is_inactive_and_nothing_changes()
    {
        // Every test and every non-OpenBao deployment: the connection string carries its own password, and none
        // of this machinery is wired in at all.
        Assert.False(new RefreshableDatabasePassword(null, NullLogger.Instance).IsActive);
    }

    private sealed class ThrowingAfterFirst : IDatabasePasswordProvider
    {
        private readonly Action _onCall;
        private int _calls;

        public ThrowingAfterFirst(Action onCall) => _onCall = onCall;

        public ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken)
        {
            _onCall();
            return ++_calls == 1
                ? ValueTask.FromResult("p1")
                : throw new InvalidOperationException("the secrets store is unreachable");
        }
    }

    private sealed class SlowFake : IDatabasePasswordProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;

        public void Release() => _gate.TrySetResult();

        public async ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await _gate.Task;
            return "p1";
        }
    }
}
