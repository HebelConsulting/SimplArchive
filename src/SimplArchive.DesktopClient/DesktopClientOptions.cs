using SimplArchive.ApiClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient;

// Configuration for the desktop fat client — see ADR "Cross-platform desktop fat client (Avalonia)". Points
// at the running Api and authenticates via OAuth 2.0 Authorization Code + PKCE using a fixed loopback
// redirect (RFC 8252 "OAuth for Native Apps"). The `simplarchive-desktop` public client and this redirect
// URI are seeded into OpenIddict by the Api on startup.
public static class DesktopClientOptions
{
    // What the desktop supplies to the shared API client (ADR 0917), set before anything reads the address, which is
    // also what every test does first: its client id, its envelope opener (certificate store, then card) and its
    // session store (the OS secret store). The logger is supplied where logging is configured (DesktopLog).
    static DesktopClientOptions()
    {
        ApiClientSettings.ClientId = ClientId;
        ApiClientSettings.EnvelopeOpener = DesktopEnvelopeOpener.Instance;
        ApiClientSettings.Sessions = TokenSessions.Current;
    }

    // Settable (not const) so tests can retarget the client at a self-hosted API on an ephemeral port; defaults
    // to the local dev/Compose endpoint. The shared client's setting IS this value, so the two cannot disagree.
    public static string ApiBaseUrl
    {
        get => ApiClientSettings.ApiRootUrl;
        set => ApiClientSettings.ApiRootUrl = value;
    }

    public const string ClientId = "simplarchive-desktop";

    // Fixed loopback port the app listens on for the OAuth redirect; the same URI is registered on the server.
    public const int LoopbackPort = 8765;

    public static string RedirectUri => $"http://127.0.0.1:{LoopbackPort}/callback";

    // Only "openid" is registered as a scope on the server; the email claim still reaches the id_token via
    // the authorization endpoint's SetDestinations (ADR 0211), so it needn't be requested as a scope.
    // "offline_access" is what ASKS for a refresh token; without it the server issues none however many flows it
    // allows, and the client is left holding one access token until it expires (the hour-long 401).
    public const string Scopes = "openid offline_access";
}
