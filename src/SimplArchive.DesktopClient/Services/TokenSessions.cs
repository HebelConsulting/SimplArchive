using System.Collections.Concurrent;
using SimplArchive.ApiClient;

namespace SimplArchive.DesktopClient.Services;


/// <summary>
/// The tokens held for each configured server, keyed by that server's API-root URL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per PROFILE, not per application.</b> A single session may talk to several deployments — the desktop's
/// server manager exists precisely so somebody administering production, integration and a local stack can move
/// between them — and one token slot for all of them means whichever was signed into last silently decides who
/// every other window is. Keyed by the API-root URL because that is what identifies an installation to this
/// client (see <see cref="ServerProfile"/>: deliberately not a tenant, which is resolved after login).
/// </para>
/// <para>
/// <b>Only the REFRESH token is persisted, and only to the OS secret store.</b> The access token is short-lived
/// and would be stale before it was ever read again, so keeping it would buy nothing and leave a credential
/// lying about. The refresh token goes to the platform's own store (see <see cref="ISecretStore"/>) — never to
/// servers.json, which sits in plaintext beside the window layout.
/// </para>
/// <para>
/// Where the platform has no store, sessions live in memory for the run and the user signs in once per launch,
/// exactly as before. That is a smaller promise, kept, rather than a larger one broken quietly.
/// </para>
/// </remarks>
public sealed class TokenSessions : ITokenSessionStore
{
    private readonly ConcurrentDictionary<string, TokenSession> _byServer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shared store. One per process, since it is keyed by server rather than by window.</summary>
    public static TokenSessions Current { get; } = new();

    /// <summary>Normalised so trailing-slash differences do not split one server into two entries.</summary>
    private static string Key(string apiRootUrl) => apiRootUrl.TrimEnd('/');

    /// <summary>
    /// This run's session for a server, restoring one from the secret store on first ask.
    /// </summary>
    /// <remarks>
    /// A restored session carries NO access token and an expiry already past, so it reads as "needs renewal"
    /// without any special case: the first request through the renewing handler exchanges the refresh token for
    /// a fresh access token, which is precisely what "still signed in from last time" means.
    /// </remarks>
    public TokenSession? For(string apiRootUrl)
    {
        var key = Key(apiRootUrl);
        if (_byServer.GetValueOrDefault(key) is { } session)
        {
            return session;
        }

        if (SecretStores.Current.Read(key) is not { Length: > 0 } refreshToken)
        {
            return null;
        }

        var restored = new TokenSession(string.Empty, refreshToken, DateTimeOffset.MinValue);
        _byServer[key] = restored;
        return restored;
    }

    /// <summary>
    /// Records the session a completed sign-in produced, for the server it was performed against.
    /// </summary>
    /// <remarks>
    /// Called BEFORE the api client is built: the client's handler reads the current token per request, so a
    /// client constructed against an empty session would send its first request unauthenticated and then try to
    /// "renew" from nothing. Lives here rather than at the call site because the ordering is a fact about
    /// sessions, not about the window that happens to sign in — and because MainWindowViewModel is on the
    /// standing-debt list and may only get smaller (#466).
    /// </remarks>
    public void Record(string apiRootUrl, OidcLoopbackAuthenticator.AuthResult result) =>
        Set(apiRootUrl, new TokenSession(result.AccessToken, result.RefreshToken, result.ExpiresAt));

    /// <summary>Records a session, persisting its refresh token so the next launch starts signed in.</summary>
    public void Set(string apiRootUrl, TokenSession session)
    {
        var key = Key(apiRootUrl);
        _byServer[key] = session;

        if (session.RefreshToken is { Length: > 0 } refreshToken)
        {
            // Best-effort: a locked keychain or an absent secret-tool means this run keeps working and the next
            // one asks for a password. A failure to persist must never fail the sign-in that just succeeded.
            SecretStores.Current.Write(key, refreshToken);
        }
        else
        {
            // The server issued none — an older deployment, say. Drop any stale one rather than leaving a token
            // behind that no longer corresponds to this session.
            SecretStores.Current.Delete(key);
        }
    }

    /// <summary>Forgets one server's session — after a sign-out, or a renewal that cannot be recovered.</summary>
    /// <remarks>
    /// Clears the STORE too. A refresh token that has been refused is worthless, and leaving it would make every
    /// later launch begin with a failed renewal before falling back to the logon window.
    /// </remarks>
    public void Clear(string apiRootUrl)
    {
        var key = Key(apiRootUrl);
        _byServer.TryRemove(key, out _);
        SecretStores.Current.Delete(key);
    }

    /// <summary>Forgets every session in this run. Used when the whole app signs out rather than one server.</summary>
    public void ClearAll()
    {
        foreach (var key in _byServer.Keys)
        {
            SecretStores.Current.Delete(key);
        }

        _byServer.Clear();
    }
}
