namespace SimplArchive.DesktopClient;

// Configuration for the desktop fat client — see ADR "Cross-platform desktop fat client (Avalonia)". Points
// at the running Api and authenticates via OAuth 2.0 Authorization Code + PKCE using a fixed loopback
// redirect (RFC 8252 "OAuth for Native Apps"). The `simplarchive-desktop` public client and this redirect
// URI are seeded into OpenIddict by the Api on startup.
public static class DesktopClientOptions
{
    // Settable (not const) so tests can retarget the client at a self-hosted API on an ephemeral port; defaults
    // to the local dev/Compose endpoint.
    public static string ApiBaseUrl { get; set; } = "http://localhost:8080";

    public const string ClientId = "simplarchive-desktop";

    // Fixed loopback port the app listens on for the OAuth redirect; the same URI is registered on the server.
    public const int LoopbackPort = 8765;

    public static string RedirectUri => $"http://127.0.0.1:{LoopbackPort}/callback";

    // Only "openid" is registered as a scope on the server; the email claim still reaches the id_token via
    // the authorization endpoint's SetDestinations (ADR 0211), so it needn't be requested as a scope.
    public const string Scopes = "openid";
}
