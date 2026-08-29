namespace SimplArchive.Api.Security;

/// <summary>
/// Where the throttle keeps its counters (ADR 0716). Two implementations, one policy: an in-process one for a
/// single-replica deployment and the test suite, and a Valkey-backed one for a deployment the HPA can scale —
/// the same shape as the OpenBao seam, where the real store appears when it is configured and the fallback is
/// what runs otherwise.
/// </summary>
/// <remarks>
/// Counters are DELIBERATELY not persisted in Postgres. A write per failed attempt would make the database
/// the amplifier of the very flood this exists to stop, and a counter that survives a restart buys nothing an
/// attacker cannot outwait anyway.
/// </remarks>
public interface IThrottleCounterStore
{
    /// <summary>Increments a decaying counter and returns its new value; the window restarts on each call.</summary>
    Task<int> CountAsync(string key, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a member to a decaying set and returns the set's size. Used for the per-address counter, which
    /// counts DISTINCT identities rather than attempts: fifty different accounts failing from one address is
    /// unambiguously a spray, while fifty failures spread over the accounts behind an office's single public
    /// address is a Monday morning.
    /// </summary>
    Task<int> CountDistinctAsync(string key, string member, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>Blocks a key for a period. A later block replaces an earlier one.</summary>
    Task BlockAsync(string key, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>How long the key stays blocked, or <c>null</c> when it is not blocked.</summary>
    Task<TimeSpan?> BlockedForAsync(string key, CancellationToken cancellationToken);

    /// <summary>Forgets a key entirely — its counter and its block.</summary>
    Task ClearAsync(string key, CancellationToken cancellationToken);
}
