using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimplArchive.Api.Security;

namespace SimplArchive.UnitTests;

// The throttling policy (ADR 0716, issue #843): what counts as too many attempts, what that costs, and — the
// half that is easy to get catastrophically wrong — what must NOT be counted together.
//
// Everything here runs against the in-process counter store, which is both the store a single-replica
// installation actually uses and the cheapest way to state the policy. The Valkey store answers the same
// contract, asserted against a real server in the E2E suite: a store whose command semantics were only ever
// guessed at is exactly the production path that would fail unnoticed.
public class SignInThrottleTests
{
    private const string Email = "VICTIM@EXAMPLE.COM";
    private const string Address = "198.51.100.7";

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A store that is down — every call throws, the way an unreachable Valkey does.</summary>
    private sealed class BrokenStore : IThrottleCounterStore
    {
        public Task<int> CountAsync(string key, TimeSpan window, CancellationToken cancellationToken) => throw new IOException("down");

        public Task<int> CountDistinctAsync(string key, string member, TimeSpan window, CancellationToken cancellationToken) => throw new IOException("down");

        public Task BlockAsync(string key, TimeSpan duration, CancellationToken cancellationToken) => throw new IOException("down");

        public Task<TimeSpan?> BlockedForAsync(string key, CancellationToken cancellationToken) => throw new IOException("down");

        public Task ClearAsync(string key, CancellationToken cancellationToken) => throw new IOException("down");
    }

    private static SignInThrottle Throttle(IThrottleCounterStore store, SignInThrottleOptions? options = null) =>
        new(store, Options.Create(options ?? new SignInThrottleOptions()), NullLogger<SignInThrottle>.Instance);

    private static async Task FailAsync(ISignInThrottle throttle, int times, SignInSurface surface = SignInSurface.Login, string identity = Email, string? address = Address)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            await throttle.RecordFailureAsync(surface, identity, address);
        }
    }

    [Fact]
    public async Task The_free_attempts_pass_and_the_one_after_them_is_blocked()
    {
        var throttle = Throttle(new InMemoryThrottleCounterStore(new TestClock()));

        await FailAsync(throttle, 5);
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).Allowed);

        await FailAsync(throttle, 1);
        var verdict = await throttle.CheckAsync(SignInSurface.Login, Email, Address);

        Assert.False(verdict.Allowed);

        // The caller is told how long to wait, and it is the first rung — a minute, not an administrator's
        // Monday. A block a person cannot wait out is a lockout by another name.
        Assert.True(verdict.RetryAfter > TimeSpan.Zero && verdict.RetryAfter <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Each_further_run_of_failures_escalates_one_rung_and_the_ladder_stops_at_its_top()
    {
        var throttle = Throttle(new InMemoryThrottleCounterStore(new TestClock()));

        await FailAsync(throttle, 6);
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).RetryAfter <= TimeSpan.FromMinutes(1));

        await FailAsync(throttle, 5);
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).RetryAfter > TimeSpan.FromMinutes(1));

        await FailAsync(throttle, 5);
        var top = (await throttle.CheckAsync(SignInSurface.Login, Email, Address)).RetryAfter;
        Assert.True(top > TimeSpan.FromMinutes(5));

        // Fifty more attempts do not grow the block. The escalation is a deterrent, not a route to a
        // permanent lock an attacker can inflict on somebody else's account.
        await FailAsync(throttle, 50);
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).RetryAfter <= top);
    }

    [Fact]
    public async Task A_successful_sign_in_forgets_the_failures_before_it()
    {
        var throttle = Throttle(new InMemoryThrottleCounterStore(new TestClock()));

        await FailAsync(throttle, 4);
        await throttle.RecordSuccessAsync(SignInSurface.Login, Email, Address);

        // Four more would have blocked without the reset — the person who mistyped four times and then got it
        // right must not be one slip away from a lockout for the rest of the window.
        await FailAsync(throttle, 4);

        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).Allowed);
    }

    [Fact]
    public async Task A_lapsed_window_starts_from_zero()
    {
        var clock = new TestClock();
        var throttle = Throttle(new InMemoryThrottleCounterStore(clock));

        await FailAsync(throttle, 5);
        clock.Advance(TimeSpan.FromMinutes(16));
        await FailAsync(throttle, 5);

        // The counter decays: ten failures spread over an hour are a forgetful person, and the tenth must not
        // be charged for the first.
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).Allowed);
    }

    [Fact]
    public async Task The_surfaces_do_not_share_a_budget()
    {
        var throttle = Throttle(new InMemoryThrottleCounterStore(new TestClock()));

        // A mail client left running with a stale app-password fails forever, unattended. If that counted
        // against the login page, one forgotten device would lock its owner out of the workbench — a support
        // call this project would deserve.
        await FailAsync(throttle, 20, SignInSurface.Imap);

        Assert.False((await throttle.CheckAsync(SignInSurface.Imap, Email, Address)).Allowed);
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).Allowed);
    }

    [Fact]
    public async Task One_identity_failing_endlessly_does_not_block_the_address()
    {
        var throttle = Throttle(new InMemoryThrottleCounterStore(new TestClock()));

        await FailAsync(throttle, 200);

        // THE property that makes an address counter safe to ship. Behind a NAT gateway — or the reverse
        // proxy every user of a Helm install arrives through — one person's stuck client is indistinguishable
        // from the whole company's traffic, so a plain per-address FAILURE count is a self-inflicted outage.
        // Only DISTINCT identities count, and one of them is one however hard it tries.
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, "SOMEBODY.ELSE@EXAMPLE.COM", Address)).Allowed);
    }

    [Fact]
    public async Task Enough_distinct_identities_from_one_address_block_that_address()
    {
        var throttle = Throttle(new InMemoryThrottleCounterStore(new TestClock()));

        // One password against twenty-six accounts: the shape a per-identity counter cannot see, because no
        // single account ever reaches its own limit.
        for (var account = 0; account < 26; account++)
        {
            await throttle.RecordFailureAsync(SignInSurface.Login, $"USER{account}@EXAMPLE.COM", Address);
        }

        Assert.False((await throttle.CheckAsync(SignInSurface.Login, "USER99@EXAMPLE.COM", Address)).Allowed);

        // …and only for that address. The spray is refused where it comes from, not everywhere.
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, "USER99@EXAMPLE.COM", "203.0.113.9")).Allowed);
    }

    [Fact]
    public async Task A_counter_store_that_is_down_lets_the_attempt_through()
    {
        var throttle = Throttle(new BrokenStore());

        // Fail OPEN, deliberately (ADR 0716): the credential check itself is untouched, so an unreachable
        // Valkey costs a defence in depth. Failing closed would cost every sign-in in the installation.
        await throttle.RecordFailureAsync(SignInSurface.Login, Email, Address);
        await throttle.RecordSuccessAsync(SignInSurface.Login, Email, Address);

        Assert.True((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).Allowed);
    }

    [Fact]
    public async Task A_configuration_that_would_allow_unlimited_guessing_is_corrected()
    {
        var throttle = Throttle(
            new InMemoryThrottleCounterStore(new TestClock()),
            new SignInThrottleOptions { IdentityFreeAttempts = 0, AddressFreeIdentities = -1 });

        // Zero free attempts would block every first attempt; a negative one is nonsense. Neither may become
        // "throttling is off" — a typo must not be the way a deployment loses this control.
        await FailAsync(throttle, 20);

        Assert.False((await throttle.CheckAsync(SignInSurface.Login, Email, Address)).Allowed);
        Assert.True((await throttle.CheckAsync(SignInSurface.Login, "FRESH@EXAMPLE.COM", "203.0.113.10")).Allowed);
    }
}
