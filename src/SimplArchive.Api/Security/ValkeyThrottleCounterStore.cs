using StackExchange.Redis;

namespace SimplArchive.Api.Security;

/// <summary>
/// The shared counter store, used when <c>ConnectionStrings:Valkey</c> is set (ADR 0716) — the same
/// connection the SignalR backplane already needs once the HPA scales past one pod, because a throttle whose
/// counters are per-replica hands an attacker one budget per replica.
/// </summary>
/// <remarks>
/// Every key carries a TTL, so the store needs no housekeeping and an attacker cannot fill it: the counters
/// expire on their own whether or not anyone looks at them again. The prefix keeps this application's keys
/// apart from anything else sharing the instance, as the backplane's channel prefix does.
/// </remarks>
public sealed class ValkeyThrottleCounterStore : IThrottleCounterStore
{
    private const string Prefix = "simplarchive:throttle:";

    private readonly IConnectionMultiplexer _multiplexer;

    public ValkeyThrottleCounterStore(IConnectionMultiplexer multiplexer) => _multiplexer = multiplexer;

    public async Task<int> CountAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        var db = _multiplexer.GetDatabase();
        var count = await db.StringIncrementAsync(Prefix + key);
        await db.KeyExpireAsync(Prefix + key, window);

        return (int)Math.Min(count, int.MaxValue);
    }

    public async Task<int> CountDistinctAsync(string key, string member, TimeSpan window, CancellationToken cancellationToken)
    {
        var db = _multiplexer.GetDatabase();
        await db.SetAddAsync(Prefix + key, member);
        await db.KeyExpireAsync(Prefix + key, window);

        return (int)Math.Min(await db.SetLengthAsync(Prefix + key), int.MaxValue);
    }

    public Task BlockAsync(string key, TimeSpan duration, CancellationToken cancellationToken) =>
        _multiplexer.GetDatabase().StringSetAsync(Prefix + key, "blocked", duration);

    public async Task<TimeSpan?> BlockedForAsync(string key, CancellationToken cancellationToken)
    {
        // The TTL IS the answer: a block is a key that exists until it does not, so there is no separate
        // deadline to store and nothing to compare clocks over between replicas.
        var remaining = await _multiplexer.GetDatabase().KeyTimeToLiveAsync(Prefix + key);

        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public Task ClearAsync(string key, CancellationToken cancellationToken) =>
        _multiplexer.GetDatabase().KeyDeleteAsync(Prefix + key);
}
