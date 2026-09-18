using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Holds the runtime database password, refreshing it on demand rather than on a timer (#1274).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the timer.</b> Npgsql's periodic password provider refreshes on a schedule, and a schedule does
/// not run while the host is suspended. A rotation during a sleep therefore leaves Npgsql holding a dead
/// password, and every connection fails <c>28P01 password authentication failed</c> until the timer next fires
/// or somebody restarts the process. Observed on the dev stack after a hibernation: the container reported
/// healthy, every request 500'd, and a restart fixed it instantly.
/// </para>
/// <para>
/// <b>Why a cache is not optional here.</b> Asking the secrets store on every physical connection open would
/// fix the staleness and introduce something worse: an OpenBao blip would become an app outage, where today it
/// is invisible. So the steady state is a memory read, and the store is consulted only when the value is older
/// than <see cref="Ttl"/> or has been explicitly invalidated.
/// </para>
/// <para>
/// <b>Keep-old-on-failure is preserved deliberately.</b> A refresh that fails returns the cached password and
/// lets the caller carry on — the same behaviour Npgsql's <c>failureRefreshInterval</c> gave, and the reason a
/// brief secrets-store outage must not take down an app whose current credential still works. The one
/// exception is a password we have been TOLD is wrong (see <see cref="Invalidate"/>): serving that again is
/// not resilience, it is repeating a known failure.
/// </para>
/// <para>
/// <b>Single-flight.</b> Invalidation is usually followed by a burst of connection opens, so without it every
/// one of them would call OpenBao at once — a self-inflicted stampede on the store we are trying not to lean
/// on. One refresh runs; the rest await its result.
/// </para>
/// </remarks>
public sealed class RefreshableDatabasePassword
{
    /// <summary>
    /// How long a fetched password is served without re-asking. Short enough that an ordinary rotation is
    /// picked up without anyone noticing, long enough that connection opens are not gated on an HTTP call.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IDatabasePasswordProvider? _provider;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    // ONE field holding both halves, swapped by reference. The password and the time it was fetched are read
    // together outside the lock, and two separate fields can be read half-updated — a torn DateTimeOffset (16
    // bytes, not atomic) would give a freshness verdict about a moment that never happened. A reference
    // assignment is atomic, so a reader sees either the whole old snapshot or the whole new one.
    private volatile Snapshot? _current;

    // Written by the connection interceptor, on a different thread from the readers. Volatile so the next
    // connection sees it rather than a cached register — the entire point is that it takes effect immediately.
    private volatile bool _knownBad;

    /// <param name="provider">
    /// The secrets-store reader, or <c>null</c> when none is registered — every test and any deployment whose
    /// connection string carries its own password. Nullable rather than absent from the container because a
    /// service that may not exist cannot be registered as a nullable singleton, and an "is it there" question
    /// asked at resolution time reads worse than one asked of the object itself.
    /// </param>
    public RefreshableDatabasePassword(IDatabasePasswordProvider? provider, ILogger logger, TimeProvider? time = null)
    {
        _provider = provider;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether a secrets store supplies the password at all; false leaves the connection string's own.</summary>
    public bool IsActive => _provider is not null;

    /// <summary>
    /// Marks the held password as known-bad, so the next request re-reads it instead of repeating a failure.
    /// </summary>
    /// <remarks>
    /// Called when the database itself has told us the password is wrong (<c>28P01</c>). That is the cheapest
    /// and most reliable signal there is: the database is the authority on whether a credential is current, and
    /// a failed authentication is it saying so directly — which no timer can know and no health check can infer.
    /// </remarks>
    public void Invalidate()
    {
        _knownBad = true;
        _logger.LogWarning(
            "The database refused the current password (28P01), so it will be re-read from the secrets store "
            + "before the next connection. This is expected once after a credential rotation; repeatedly means "
            + "the store is handing out a password the database does not have.");
    }

    public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        if (Fresh(_current) is { } fresh)
        {
            return fresh;
        }

        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            // Re-checked INSIDE the lock: everyone queued behind one refresh wants its result, not another
            // round trip each. This is the whole point of the single-flight.
            if (Fresh(_current) is { } arrived)
            {
                return arrived;
            }

            var password = await _provider!.GetPasswordAsync(cancellationToken);
            _current = new Snapshot(password, _time.GetUtcNow());
            _knownBad = false;
            return password;
        }
        catch (Exception e) when (_current is { } held && !_knownBad)
        {
            // Keep serving. The credential we hold still works — nothing has said otherwise — and refusing
            // connections because a secrets store is briefly unreachable would be a self-inflicted outage.
            //
            // NOT reached when the password is known-bad: the filter excludes it deliberately, because serving
            // a credential the database has already refused is not resilience, it is repeating a failure with
            // the error hidden.
            _logger.LogWarning(e,
                "Could not refresh the database password; continuing with the one already in use. It will be "
                + "retried on the next connection after the cache expires.");
            return held.Password;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private string? Fresh(Snapshot? snapshot) =>
        snapshot is not null && !_knownBad && _time.GetUtcNow() - snapshot.FetchedAt < Ttl
            ? snapshot.Password
            : null;

    private sealed record Snapshot(string Password, DateTimeOffset FetchedAt);
}
