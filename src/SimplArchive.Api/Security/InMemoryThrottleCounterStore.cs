using System.Collections.Concurrent;

namespace SimplArchive.Api.Security;

/// <summary>
/// The counter store a deployment gets when no Valkey is configured (ADR 0716) — in this process, so its
/// counters are this replica's. That is exactly right for the deployments that have no Valkey: the compose
/// stack, a single-replica install and the test suite all run one API process, so "this replica" is "the
/// installation". A multi-replica install configures <c>ConnectionStrings:Valkey</c> and gets the shared one;
/// without it, an attacker spread across replicas would get one budget per replica.
/// </summary>
public sealed class InMemoryThrottleCounterStore : IThrottleCounterStore
{
    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private readonly TimeProvider _time;

    public InMemoryThrottleCounterStore(TimeProvider time) => _time = time;

    public Task<int> CountAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        var slot = Renew(key, window);
        lock (slot)
        {
            return Task.FromResult(++slot.Count);
        }
    }

    public Task<int> CountDistinctAsync(string key, string member, TimeSpan window, CancellationToken cancellationToken)
    {
        var slot = Renew(key, window);
        lock (slot)
        {
            (slot.Members ??= []).Add(member);

            return Task.FromResult(slot.Members.Count);
        }
    }

    public Task BlockAsync(string key, TimeSpan duration, CancellationToken cancellationToken)
    {
        Renew(key, duration);

        return Task.CompletedTask;
    }

    public Task<TimeSpan?> BlockedForAsync(string key, CancellationToken cancellationToken)
    {
        if (!_slots.TryGetValue(key, out var slot))
        {
            return Task.FromResult<TimeSpan?>(null);
        }

        var remaining = slot.ExpiresAt - _time.GetUtcNow();

        return Task.FromResult<TimeSpan?>(remaining > TimeSpan.Zero ? remaining : null);
    }

    public Task ClearAsync(string key, CancellationToken cancellationToken)
    {
        _slots.TryRemove(key, out _);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Fetches a key's slot, restarting its window — and replacing it outright if it had already lapsed, so
    /// an expired counter never resumes from its old value.
    /// </summary>
    private Slot Renew(string key, TimeSpan window)
    {
        var now = _time.GetUtcNow();
        var slot = _slots.AddOrUpdate(
            key,
            _ => new Slot { ExpiresAt = now + window },
            (_, existing) =>
            {
                lock (existing)
                {
                    if (existing.ExpiresAt <= now)
                    {
                        existing.Count = 0;
                        existing.Members = null;
                    }

                    existing.ExpiresAt = now + window;
                }

                return existing;
            });

        Sweep(now);

        return slot;
    }

    /// <summary>
    /// Drops lapsed slots. Nothing else removes them, and the keys are attacker-chosen — an unswept dictionary
    /// is a memory-exhaustion vector reachable by anyone who can reach the login page. Only runs once the map
    /// is big enough to be worth walking, and only on the write path (a failed attempt), never on a read.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        if (_slots.Count < 1024)
        {
            return;
        }

        foreach (var (key, slot) in _slots)
        {
            if (slot.ExpiresAt <= now)
            {
                _slots.TryRemove(key, out _);
            }
        }
    }

    private sealed class Slot
    {
        public int Count;
        public HashSet<string>? Members;
        public DateTimeOffset ExpiresAt;
    }
}
