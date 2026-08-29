using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using SimplArchive.Api.Security;
using StackExchange.Redis;

namespace SimplArchive.EndToEndTests;

// The two counter stores answer ONE contract (ADR 0716), so it is written once and run against both.
//
// The Valkey store is the path a scaled deployment uses and the one nothing else here exercises: its
// semantics are Valkey's, not ours — what INCR returns, whether EXPIRE restarts a window, what TTL says about
// a key that has gone. A store whose commands were only ever reasoned about is exactly the production path
// that fails unnoticed, which is why this one talks to a real server rather than to a mock of one.
[Trait("Area", "e2e-2")]
public class ThrottleCounterStoreTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task The_in_process_store_answers_the_contract() =>
        await AssertContractAsync(new InMemoryThrottleCounterStore(TimeProvider.System));

    [Fact]
    public async Task Valkey_answers_the_same_contract()
    {
        // The image the compose stack runs, so the tests measure what a deployment will actually meet.
        await using var valkey = new ContainerBuilder()
            .WithImage("valkey/valkey:8.1.1-alpine")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
            .Build();

        await valkey.StartAsync();

        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(
            $"{valkey.Hostname}:{valkey.GetMappedPublicPort(6379)}");

        await AssertContractAsync(new ValkeyThrottleCounterStore(multiplexer));
    }

    private static async Task AssertContractAsync(IThrottleCounterStore store)
    {
        var key = $"contract-{Guid.NewGuid():N}";

        // ---- a counter counts, from one ---------------------------------------------------------------
        Assert.Equal(1, await store.CountAsync(key, Window, CancellationToken.None));
        Assert.Equal(2, await store.CountAsync(key, Window, CancellationToken.None));

        // ---- a distinct counter counts identities, not attempts ---------------------------------------
        var spray = $"{key}-spray";
        Assert.Equal(1, await store.CountDistinctAsync(spray, "alice", Window, CancellationToken.None));
        Assert.Equal(1, await store.CountDistinctAsync(spray, "alice", Window, CancellationToken.None));
        Assert.Equal(2, await store.CountDistinctAsync(spray, "bob", Window, CancellationToken.None));

        // ---- a block exists, reports what remains, and is replaced rather than extended ----------------
        var blocked = $"{key}-blocked";
        Assert.Null(await store.BlockedForAsync(blocked, CancellationToken.None));

        await store.BlockAsync(blocked, TimeSpan.FromMinutes(5), CancellationToken.None);
        var remaining = await store.BlockedForAsync(blocked, CancellationToken.None);
        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

        await store.BlockAsync(blocked, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(await store.BlockedForAsync(blocked, CancellationToken.None) <= TimeSpan.FromMinutes(1));

        // ---- clearing forgets it entirely -------------------------------------------------------------
        await store.ClearAsync(blocked, CancellationToken.None);
        Assert.Null(await store.BlockedForAsync(blocked, CancellationToken.None));

        await store.ClearAsync(key, CancellationToken.None);
        Assert.Equal(1, await store.CountAsync(key, Window, CancellationToken.None));
    }

    [Fact]
    public async Task A_window_that_has_lapsed_starts_the_count_again()
    {
        // Asserted against the in-process store only: it is the one whose expiry WE implement. Valkey's own
        // TTL is not this project's to re-test, and a test that waited out a real one would trade a minute of
        // suite time for a fact the server already guarantees.
        var clock = new FrozenClock();
        var store = new InMemoryThrottleCounterStore(clock);
        var key = $"lapse-{Guid.NewGuid():N}";

        Assert.Equal(1, await store.CountAsync(key, Window, CancellationToken.None));
        clock.Advance(Window + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await store.CountAsync(key, Window, CancellationToken.None));
    }
}
