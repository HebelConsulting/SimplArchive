
namespace SimplArchive.UiEndToEndTests;

// Desktop-side helper: obtains a demo-admin User access token by driving the real OAuth2 Authorization Code +
// PKCE flow over HTTP (the same flow the SPA performs) — used to point the DesktopClient's SimplArchiveApiClient
// at the self-hosted API without a loopback-browser login. (The web UI project's Ui.cs additionally has the
// Playwright LoginAsync; this desktop copy is HTTP-only, no browser.)
internal static class Ui
{
    // Forwards to SimplArchive.SelfHosting (#1358). The flow lived HERE until the manual-capture harness
    // needed the same thing, which would have been a third copy of a multi-step protocol dance — so it moved
    // next to the engine both callers already wrap, and this stays as the name the suite calls.
    public static Task<string> GetUserTokenAsync(string baseUrl, string? email = null, string? password = null) =>
        SimplArchive.SelfHosting.SelfHostedLogin.GetUserTokenAsync(baseUrl, email, password);
}
