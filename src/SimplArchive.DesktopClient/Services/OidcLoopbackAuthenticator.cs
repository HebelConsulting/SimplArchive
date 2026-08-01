using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// OAuth 2.0 Authorization Code + PKCE via a loopback redirect — the standard for native apps (RFC 8252).
// Opens the system browser to the Api's authorize endpoint, receives the code on a local HttpListener, and
// exchanges it for tokens. No client secret (a public client). See ADR "Cross-platform desktop fat client".
public sealed class OidcLoopbackAuthenticator
{
    private static readonly HttpClient Http = new();

    public sealed record AuthResult(string AccessToken, string? Email);

    // forceLogin adds prompt=login so the server re-authenticates even if the system browser still holds a
    // session cookie — used after a Log out, so a different tenant/user can sign in (ADR "Desktop logout").
    // loginHint (an email) is passed as the OIDC login_hint so the server login page pre-fills the address
    // (ADR "Browser-only desktop login + login_hint").
    public async Task<AuthResult?> AuthenticateAsync(bool forceLogin = false, string? loginHint = null, CancellationToken cancellationToken = default)
    {
        var codeVerifier = Base64Url(RandomBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = Base64Url(RandomBytes(16));

        // Discover the endpoints rather than hardcoding /connect/* paths.
        var discovery = await Http.GetFromJsonAsync<JsonElement>(
            $"{DesktopClientOptions.ApiBaseUrl}/.well-known/openid-configuration", cancellationToken);
        var authorizationEndpoint = discovery.GetProperty("authorization_endpoint").GetString()!;
        var tokenEndpoint = discovery.GetProperty("token_endpoint").GetString()!;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{DesktopClientOptions.LoopbackPort}/");
        listener.Start();

        var authorizeUrl =
            $"{authorizationEndpoint}?client_id={DesktopClientOptions.ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(DesktopClientOptions.RedirectUri)}" +
            $"&response_type=code&scope={Uri.EscapeDataString(DesktopClientOptions.Scopes)}" +
            $"&code_challenge={codeChallenge}&code_challenge_method=S256&state={state}" +
            (forceLogin ? "&prompt=login" : string.Empty) +
            (string.IsNullOrWhiteSpace(loginHint) ? string.Empty : $"&login_hint={Uri.EscapeDataString(loginHint)}");
        SystemBrowser.Open(authorizeUrl);

        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var code = context.Request.QueryString["code"];
        var returnedState = context.Request.QueryString["state"];

        var body = Encoding.UTF8.GetBytes(
            "<html><body style='font-family:sans-serif;padding:40px'>You can close this window and return to SimplArchive.</body></html>");
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(body, cancellationToken);
        context.Response.Close();

        if (string.IsNullOrEmpty(code) || returnedState != state)
        {
            return null;
        }

        using var tokenResponse = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DesktopClientOptions.RedirectUri,
            ["client_id"] = DesktopClientOptions.ClientId,
            ["code_verifier"] = codeVerifier,
        }), cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();

        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var email = tokens.TryGetProperty("id_token", out var idToken) ? ReadEmailFromJwt(idToken.GetString()) : null;
        return new AuthResult(accessToken, email);
    }

    // Opens the server-rendered passkey-management page (ADR "Desktop passkey management") in the system
    // browser and waits for it to hand back to a loopback. A native window can't run the WebAuthn attestation
    // ceremony, so registration happens in the browser (against the auth-server cookie session the OIDC login
    // already established); on success the page redirects to http://127.0.0.1:<port>/passkey-done. Returns
    // true if a passkey was added, so the caller can refresh. Never throws for a user close/timeout.
    public async Task<bool> ManagePasskeysAsync(CancellationToken cancellationToken = default)
    {
        var port = FreeLoopbackPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        SystemBrowser.Open($"{DesktopClientOptions.ApiBaseUrl}/Account/Passkeys?loopback={port}");

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        var added = context.Request.QueryString["added"] == "1";

        var body = Encoding.UTF8.GetBytes(
            "<html><body style='font-family:sans-serif;padding:40px'>You can close this window and return to SimplArchive.</body></html>");
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(body, cancellationToken);
        context.Response.Close();

        return added;
    }

    // A free ephemeral loopback port for the passkey hand-off (distinct from the OIDC login's fixed port so
    // the two never collide). Binding to port 0 lets the OS pick a free one; we release it immediately and
    // reuse the number for the HttpListener (the small reuse window is acceptable for a one-shot local flow).
    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string? ReadEmailFromJwt(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(Base64UrlDecode(parts[1]));
            return payload.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
