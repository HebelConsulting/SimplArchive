using System.Collections.Concurrent;

namespace SimplArchive.Api.WebDav;

// In-memory WebDAV lock store (ADR "WebDAV hardening") — exclusive write locks keyed by tenant + resource path.
// Ephemeral (per-process, timeout-based); adequate for the gateway's first real-lock slice. A persistent,
// multi-instance lock table is deferred. A lock is created by LOCK and enforced on the mutating methods: a
// *different* owner that doesn't present the matching lock token (via If / Lock-Token) gets 423 Locked.
public sealed class WebDavLockStore
{
    public sealed record LockInfo(string Token, Guid Owner, DateTimeOffset ExpiresAt, DateTimeOffset LockedAt);

    private readonly ConcurrentDictionary<string, LockInfo> _locks = new();

    private static string Key(Guid tenantId, string path) => $"{tenantId:D}:{path.Trim('/')}";

    public LockInfo? Get(Guid tenantId, string path, DateTimeOffset now)
    {
        var key = Key(tenantId, path);
        if (_locks.TryGetValue(key, out var info))
        {
            if (info.ExpiresAt > now)
            {
                return info;
            }

            _locks.TryRemove(new KeyValuePair<string, LockInfo>(key, info)); // expired — sweep lazily
        }

        return null;
    }

    // Acquire a new exclusive lock, or refresh the caller's own existing one. Returns null when another owner
    // currently holds it.
    public LockInfo? TryLock(Guid tenantId, string path, Guid owner, TimeSpan timeout, DateTimeOffset now)
    {
        var key = Key(tenantId, path);
        while (true)
        {
            if (_locks.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            {
                if (existing.Owner != owner)
                {
                    return null; // held by someone else
                }

                var refreshed = existing with { ExpiresAt = now + timeout };
                if (_locks.TryUpdate(key, refreshed, existing))
                {
                    return refreshed;
                }

                continue; // lost a race — retry
            }

            var created = new LockInfo($"opaquelocktoken:{Guid.NewGuid()}", owner, now + timeout, now);
            if (existing is null ? _locks.TryAdd(key, created) : _locks.TryUpdate(key, created, existing))
            {
                return created;
            }
        }
    }

    public bool Unlock(Guid tenantId, string path, string token)
    {
        var key = Key(tenantId, path);
        return _locks.TryGetValue(key, out var info) && info.Token == token
            && _locks.TryRemove(new KeyValuePair<string, LockInfo>(key, info));
    }

    // Whether a mutating op on this path is blocked: an unexpired lock held by a *different* owner whose token
    // the request didn't present.
    public bool IsBlocked(Guid tenantId, string path, Guid caller, IReadOnlyCollection<string> presentedTokens, DateTimeOffset now)
    {
        var info = Get(tenantId, path, now);
        return info is not null && info.Owner != caller && !presentedTokens.Contains(info.Token);
    }
}
