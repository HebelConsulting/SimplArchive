using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SimplArchive.Api.Security;

/// <summary>
/// The one throttling policy every credential-verifying surface shares (ADR 0716, issue #843). The surfaces
/// differ in how they name an identity and how they refuse; what counts as too many attempts must not.
/// </summary>
public sealed class SignInThrottle : ISignInThrottle
{
    private readonly IThrottleCounterStore _store;
    private readonly ILogger<SignInThrottle> _logger;
    private readonly int _identityFreeAttempts;
    private readonly int _addressFreeIdentities;
    private readonly TimeSpan _window;
    private readonly IReadOnlyList<TimeSpan> _penalties;

    public SignInThrottle(IThrottleCounterStore store, IOptions<SignInThrottleOptions> options, ILogger<SignInThrottle> logger)
    {
        _store = store;
        _logger = logger;

        var configured = options.Value;

        // A configuration typo must not quietly disable the control, and must not stop the app either — so
        // each value is corrected to something that still throttles, and the correction is named (ADR 0626).
        _identityFreeAttempts = Corrected(configured.IdentityFreeAttempts, 5, nameof(SignInThrottleOptions.IdentityFreeAttempts));
        _addressFreeIdentities = Corrected(configured.AddressFreeIdentities, 25, nameof(SignInThrottleOptions.AddressFreeIdentities));
        _penalties = configured.Penalties is { Count: > 0 } ladder
            ? [.. ladder]
            : [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
        _window = configured.Window > TimeSpan.Zero ? configured.Window : TimeSpan.FromMinutes(15);
    }

    private int Corrected(int value, int fallback, string name)
    {
        if (value >= 1)
        {
            return value;
        }

        _logger.LogWarning(
            "SignInThrottle:{Setting} was {Configured}, which would allow unlimited guessing; using {Fallback}",
            name, value, fallback);

        return fallback;
    }

    public async Task<SignInThrottleVerdict> CheckAsync(
        SignInSurface surface, string identity, string? address, CancellationToken cancellationToken = default)
    {
        try
        {
            // Both questions are asked at once rather than one after the other. This runs on EVERY request
            // that carries a credential, and a DAV client turns one user action into dozens of them — so a
            // second sequential round trip to Valkey would be paid by the honest case, forever, to answer a
            // question that is almost always "no".
            var byIdentity = _store.BlockedForAsync(Blocked(IdentityKey(surface, identity)), cancellationToken);
            var byAddress = address is null
                ? Task.FromResult<TimeSpan?>(null)
                : _store.BlockedForAsync(Blocked(AddressKey(address)), cancellationToken);

            await Task.WhenAll(byIdentity, byAddress);

            // The identity's own block wins when both are set: it is the specific answer, and the one whose
            // Retry-After is worth telling the caller.
            var remaining = await byIdentity ?? await byAddress;

            return remaining > TimeSpan.Zero ? SignInThrottleVerdict.Refuse(remaining.Value) : SignInThrottleVerdict.Allow;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Unavailable(exception);

            return SignInThrottleVerdict.Allow;
        }
    }

    public async Task RecordFailureAsync(
        SignInSurface surface, string identity, string? address, CancellationToken cancellationToken = default)
    {
        try
        {
            var identityKey = IdentityKey(surface, identity);
            var failures = await _store.CountAsync(Counter(identityKey), _window, cancellationToken);

            if (PenaltyFor(failures, _identityFreeAttempts) is { } penalty)
            {
                await _store.BlockAsync(Blocked(identityKey), penalty, cancellationToken);

                // The identity is NOT logged here: the surfaces already log their own failure with whatever
                // identifier is safe to print there (a user id, a client id, an email), and repeating it would
                // add a second, less careful copy of that decision.
                _logger.LogWarning(
                    "Sign-in throttled on {Surface} after {Failures} failed attempts for one identity; blocked for {Penalty}",
                    surface, failures, penalty);
            }

            if (address is null)
            {
                return;
            }

            var addressKey = AddressKey(address);
            var identities = await _store.CountDistinctAsync(
                Counter(addressKey), Fingerprint($"{surface}:{identity}"), _window, cancellationToken);

            if (PenaltyFor(identities, _addressFreeIdentities) is { } spray)
            {
                await _store.BlockAsync(Blocked(addressKey), spray, cancellationToken);

                _logger.LogWarning(
                    "Sign-in throttled for {Address}: {Identities} distinct identities failed from it; blocked for {Penalty}",
                    address, identities, spray);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Unavailable(exception);
        }
    }

    public async Task RecordSuccessAsync(
        SignInSurface surface, string identity, string? address, CancellationToken cancellationToken = default)
    {
        try
        {
            // Also concurrent, and for the same reason: this is the path a successful request takes.
            var identityKey = IdentityKey(surface, identity);

            await Task.WhenAll(
                _store.ClearAsync(Counter(identityKey), cancellationToken),
                _store.ClearAsync(Blocked(identityKey), cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Unavailable(exception);
        }
    }

    /// <summary>
    /// The block for a run of failures: none while the free attempts last, then one rung per further run,
    /// stopping at the top of the ladder rather than growing without bound.
    /// </summary>
    private TimeSpan? PenaltyFor(int count, int free)
    {
        if (count <= free)
        {
            return null;
        }

        var rung = Math.Min((count - free - 1) / free, _penalties.Count - 1);

        return _penalties[rung];
    }

    // Per-surface identity keys: see ISignInThrottle's note on why one stale mail client must not lock its
    // owner out of the workbench.
    private static string IdentityKey(SignInSurface surface, string identity) =>
        $"signin:identity:{surface}:{Fingerprint(identity)}";

    private static string AddressKey(string address) => $"signin:address:{Fingerprint(address)}";

    private static string Counter(string key) => $"{key}:count";

    private static string Blocked(string key) => $"{key}:blocked";

    /// <summary>
    /// Keys carry a fingerprint rather than the value. Nothing in the store needs to be legible, and a
    /// counter store that can be read for the list of accounts currently under attack — or for the addresses
    /// of the people using the installation — is a worse trade than an operator computing a hash to debug.
    /// </summary>
    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private void Unavailable(Exception exception) =>
        _logger.LogWarning(
            exception,
            "The sign-in throttle's counter store is unavailable; credential attempts are UNLIMITED until it recovers. "
            + "The credential check itself is unaffected");
}
