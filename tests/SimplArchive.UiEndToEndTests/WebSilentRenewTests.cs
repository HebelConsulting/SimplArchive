using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// How the WEB client keeps a session alive (#669, ADR 0660) — and the guard on the decision not to change it.
//
// The web renews by re-authorizing against the OpenIddict COOKIE (`prompt=none`), where the desktop rotates a
// refresh token held in the OS secret store. That asymmetry is deliberate: a refresh token in a browser lives
// in the same storage an XSS already reads, and would upgrade the blast radius from one hour of access to
// long-lived, offline-capable access — with reuse detection still on the deferred list.
//
// This is written as an ASSERTION rather than a comment because a decision nothing enforces is a decision that
// gets reversed by whoever next reads the two clients and notices they differ. If someone adds `offline_access`
// to the web client, this fails and sends them to the ADR, where the trigger that WOULD justify it is written
// down: federation (#545) putting an external IdP in the path, which is when same-origin stops holding.
[Collection(UiCollection.Name)]
public class WebSilentRenewTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSilentRenewTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_session_is_renewed_against_the_cookie_and_holds_no_refresh_token()
    {
        var page = await Ui.LoginAsync(_app);

        var connect = new List<string>();
        page.Request += (_, r) => { if (r.Url.Contains("/connect/")) connect.Add(r.Url); };

        // A reload exercises the SAME mechanism the expiry timer uses: a prompt=none authorize that succeeds
        // because the browser still holds the cookie session. Observing it here rather than waiting out the
        // hour — what is being pinned is that the path exists and works, not the timer's arithmetic.
        await page.ReloadAsync();
        await Expect(page.Locator(".wb-appbar").GetByText("Demo Admin")).ToBeAttachedAsync();
        await page.WaitForTimeoutAsync(2500);

        Assert.Contains(connect, u => u.Contains("/connect/authorize") && u.Contains("prompt=none"));
        Assert.Contains(connect, u => u.Contains("/connect/token"));

        var stored = await page.EvaluateAsync<string>(@"() => {
            for (const store of [sessionStorage, localStorage]) {
                for (let i = 0; i < store.length; i++) {
                    const k = store.key(i);
                    if (!k.startsWith('oidc.user:')) continue;
                    const v = JSON.parse(store.getItem(k));
                    return JSON.stringify({ hasRefresh: !!v.refresh_token, scope: v.scope });
                }
            }
            return '';
        }");

        Assert.False(string.IsNullOrEmpty(stored), "no oidc user in browser storage — the probe found nothing to assert about");

        // The two halves of the decision. `openid` only, and therefore no refresh token: `offline_access` is
        // what ASKS for one, so its absence from the request is the mechanism, not merely the symptom.
        Assert.Contains("\"hasRefresh\":false", stored);
        Assert.Contains("\"scope\":\"openid\"", stored);
    }
}
