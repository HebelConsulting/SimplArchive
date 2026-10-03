using Microsoft.Extensions.Logging.Abstractions;
using OtpNet;
using SimplArchive.Api.Authentication;
using SimplArchive.Api.Security;

namespace SimplArchive.UnitTests;

// A TOTP code is ONE-TIME, which is in the standard's name and was not true here (#847, A02).
//
// The verification window is one 30s step either way, so a code stayed valid for about 90 seconds — and
// without a replay store it stayed valid for ALL of it, however many times it was presented. A code read over
// a shoulder, out of a screen share, or relayed by a phishing page in real time could be replayed for the
// rest of that window, which is the whole of the attack TOTP exists to stop.
public class TotpReplayTests
{
    private static MfaService Service(IThrottleCounterStore store) =>
        new(store, NullLogger<MfaService>.Instance);

    private static (string Secret, string Code) Enrolled()
    {
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        return (secret, new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp());
    }

    [Fact]
    public async Task The_same_code_is_accepted_once_and_refused_afterwards()
    {
        var store = new InMemoryThrottleCounterStore(TimeProvider.System);
        var service = Service(store);
        var user = Guid.NewGuid();
        var (secret, code) = Enrolled();

        Assert.True(await service.VerifyTotpAsync(user, secret, code));

        // Twice more, because "refused the second time" and "refused from now on" are different claims and the
        // counter-based burn had better make the second one.
        Assert.False(await service.VerifyTotpAsync(user, secret, code));
        Assert.False(await service.VerifyTotpAsync(user, secret, code));
    }

    [Fact]
    public async Task Another_user_holding_the_same_digits_is_unaffected()
    {
        // Two authenticators CAN produce the same six digits at the same instant — there are only a million of
        // them. Burning the digits rather than the (user, timestep) would make one person's sign-in refuse
        // somebody else's, which is why the claim is keyed per user.
        var store = new InMemoryThrottleCounterStore(TimeProvider.System);
        var service = Service(store);
        var (secret, code) = Enrolled();

        Assert.True(await service.VerifyTotpAsync(Guid.NewGuid(), secret, code));
        Assert.True(await service.VerifyTotpAsync(Guid.NewGuid(), secret, code));
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_burns_nothing()
    {
        // The anti-vacuous half, and a real hazard: if a failed attempt burned the step, an attacker could
        // lock a user out of their own next sign-in by submitting a wrong code — a denial of service built
        // out of the replay defence.
        var store = new InMemoryThrottleCounterStore(TimeProvider.System);
        var service = Service(store);
        var user = Guid.NewGuid();
        var (secret, code) = Enrolled();

        Assert.False(await service.VerifyTotpAsync(user, secret, "000000"));
        Assert.True(await service.VerifyTotpAsync(user, secret, code));
    }

    [Fact]
    public async Task It_FAILS_OPEN_when_the_store_is_unreachable()
    {
        // The same posture the throttle takes for the same store (ADR 0716): failing closed would turn a cache
        // hiccup into "nobody with MFA can sign in", trading a self-inflicted outage for a defence that is
        // additive. The code's own verification is unaffected either way.
        var service = Service(new ThrowingThrottleCounterStore());
        var (secret, code) = Enrolled();

        Assert.True(await service.VerifyTotpAsync(Guid.NewGuid(), secret, code));
    }

    [Fact]
    public async Task Re_enrolling_with_a_fresh_secret_in_the_same_step_is_not_a_replay()
    {
        // THE CASE I GOT WRONG, which is why it has a test of its own. Keyed on (user, step) alone, a user
        // could spend exactly one code per 30-second step for ANY purpose — so re-enrolling with a new secret
        // seconds after using the old one was refused as a replay. It is not one: a different secret's code
        // shares nothing with the old except the clock. The E2E MFA test found this; the unit test is what
        // keeps it found.
        var store = new InMemoryThrottleCounterStore(TimeProvider.System);
        var service = Service(store);
        var user = Guid.NewGuid();

        var (first, firstCode) = Enrolled();
        var (second, secondCode) = Enrolled();

        Assert.True(await service.VerifyTotpAsync(user, first, firstCode));
        Assert.True(await service.VerifyTotpAsync(user, second, secondCode));

        // …and each is still burned within its OWN secret.
        Assert.False(await service.VerifyTotpAsync(user, first, firstCode));
        Assert.False(await service.VerifyTotpAsync(user, second, secondCode));
    }

    private sealed class ThrowingThrottleCounterStore : IThrottleCounterStore
    {
        public Task<int> CountAsync(string key, TimeSpan window, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the counter store is down");

        public Task<int> CountDistinctAsync(string key, string member, TimeSpan window, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the counter store is down");

        public Task BlockAsync(string key, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TimeSpan?> BlockedForAsync(string key, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
        public Task ClearAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
