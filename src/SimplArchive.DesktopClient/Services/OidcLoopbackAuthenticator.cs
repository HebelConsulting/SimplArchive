using System.Net;
using System.Text;

namespace SimplArchive.DesktopClient.Services;

// OAuth 2.0 Authorization Code + PKCE via a loopback redirect — the standard for native apps (RFC 8252).
// Opens the system browser to the Api's authorize endpoint, receives the code on a local HttpListener, and
// exchanges it for tokens. No client secret (a public client). See ADR "Cross-platform desktop fat client".
public sealed class OidcLoopbackAuthenticator
{
    // forceLogin adds prompt=login so the server re-authenticates even if the system browser still holds a
    // session cookie — used after a Log out, so a different tenant/user can sign in (ADR "Desktop logout").
    // loginHint (an email) is passed as the OIDC login_hint so the server login page pre-fills the address
    // (ADR "Browser-only desktop login + login_hint"). The flow itself is the shared PkceSignIn (ADR 0917); what is
    // the desktop's is receiving the redirect on a loopback listener.
    public async Task<AuthResult?> AuthenticateAsync(bool forceLogin = false, string? loginHint = null, CancellationToken cancellationToken = default)
    {
        var signIn = await PkceSignIn.StartAsync(DesktopClientOptions.ApiBaseUrl, DesktopClientOptions.ClientId,
            DesktopClientOptions.RedirectUri, DesktopClientOptions.Scopes, forceLogin, loginHint, cancellationToken);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{DesktopClientOptions.LoopbackPort}/");
        listener.Start();
        // The authorize URL itself is NOT logged: it carries the PKCE challenge and the state, and a log a user
        // pastes into a support mail must not contain either (ADR 0430's rule, which is easier to break here).
        DesktopLog.Debug("Waiting for the loopback redirect on port {Port}", DesktopClientOptions.LoopbackPort);
        SystemBrowser.Open(signIn.AuthorizeUrl);

        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var code = context.Request.QueryString["code"];
        var returnedState = context.Request.QueryString["state"];

        var body = Encoding.UTF8.GetBytes(
            "<html><body style='font-family:sans-serif;padding:40px'>You can close this window and return to SimplArchive.</body></html>");
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(body, cancellationToken);
        context.Response.Close();

        return await signIn.CompleteAsync(code, returnedState, cancellationToken);
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
}
