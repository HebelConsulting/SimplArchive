namespace SimplArchive.Api.Security;

/// <summary>
/// The doors that verify a credential (ADR 0716). Each has its OWN counters, so a mail client left running
/// with a stale app-password cannot lock its owner out of the workbench — the two secrets are different
/// secrets, and a shared counter would turn one misconfigured device into a denial of service against the
/// person who owns it.
/// </summary>
public enum SignInSurface
{
    /// <summary>The interactive login page — the account password, and the second factor behind it.</summary>
    Login,

    /// <summary>The OAuth token endpoint — service-account and platform-administrator client secrets.</summary>
    Token,

    /// <summary>WebDAV / CalDAV / CardDAV HTTP Basic — the one shared app-specific DAV password.</summary>
    Dav,

    /// <summary>The IMAP endpoint's LOGIN / AUTHENTICATE — the app-specific IMAP password.</summary>
    Imap,
}

/// <summary>
/// The answer to "may this attempt be tried at all?". <see cref="RetryAfter"/> is meaningful only when the
/// attempt is refused, and is what the surface tells the caller to wait.
/// </summary>
public readonly record struct SignInThrottleVerdict(bool Allowed, TimeSpan RetryAfter)
{
    public static SignInThrottleVerdict Allow { get; } = new(true, TimeSpan.Zero);

    public static SignInThrottleVerdict Refuse(TimeSpan retryAfter) => new(false, retryAfter);
}

/// <summary>
/// Progressive throttling of credential guessing (ADR 0716, issue #843), counted per identity AND per client
/// address: a per-identity limit alone is defeated by an attacker who tries one password against ten thousand
/// accounts, and a per-address limit alone is defeated by a botnet — and, worse, a plain per-address FAILURE
/// count behind a reverse proxy is a self-inflicted outage, because every user shares the proxy's address.
/// </summary>
/// <remarks>
/// <para>
/// This is prevention, not detection: every surface already logs its failures at Warning so a SIEM can
/// aggregate them (ADR 0430). What was missing is the wall.
/// </para>
/// <para>
/// The throttle FAILS OPEN. If its counter store is unreachable, an attempt is allowed and the outage is
/// logged at Warning — the credential check itself still runs, so failing open weakens a defence rather than
/// removing authentication, whereas failing closed would turn a Valkey hiccup into a total sign-in outage.
/// </para>
/// </remarks>
public interface ISignInThrottle
{
    /// <summary>Asks whether an attempt may be made. Call BEFORE verifying the credential.</summary>
    Task<SignInThrottleVerdict> CheckAsync(
        SignInSurface surface, string identity, string? address, CancellationToken cancellationToken = default);

    /// <summary>Records a rejected credential. The identity is whatever was CLAIMED — it need not exist.</summary>
    Task RecordFailureAsync(
        SignInSurface surface, string identity, string? address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an accepted credential, which clears that identity's counter and any block on it. It does NOT
    /// clear the address's spray counter: one credential the attacker already holds must not wipe the
    /// evidence of the thousands they were guessing beside it.
    /// </summary>
    Task RecordSuccessAsync(
        SignInSurface surface, string identity, string? address, CancellationToken cancellationToken = default);
}
