using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// Shared browser helpers — drive the real interactive OIDC login the SPA uses (SPA login button → server-rendered
// /Account/Login form → back to the authenticated SPA).
internal static partial class Ui
{
    // The web tests all share one collection fixture, so they run sequentially — one browser context is live at a
    // time. Tests get their page from LoginAsync and don't dispose the context, which used to leak ~one Chrome
    // context per test and, over the full suite on a 7 GB runner, grew until the kernel OOM-killed the test host.
    // Close the previous test's context on each login so at most one is alive; memory stays flat.
    private static IBrowserContext? _previousContext;

    public static async Task<IPage> LoginAsync(SelfHostedAppFixture app, string[]? permissions = null, bool dismissDesktopPromo = true, Action<BrowserNewContextOptions>? configureContext = null)
    {
        if (_previousContext is not null)
        {
            try { await _previousContext.CloseAsync(); } catch { /* best effort — the run is ending anyway */ }
        }

        // configureContext lets a test opt into e.g. a touch-emulated phone context (HasTouch + a phone viewport)
        // so touch-tier behaviour is exercised, not just a narrow desktop viewport (touch test tier, #360).
        var contextOptions = new BrowserNewContextOptions { AcceptDownloads = true, Permissions = permissions };
        configureContext?.Invoke(contextOptions);
        var context = await app.Browser.NewContextAsync(contextOptions);
        _previousContext = context;

        // The post-logon desktop-client promo (ADR 0505) shows a one-time modal on a fresh browser (empty
        // localStorage — which every test context is). Left to fire, its MudDialog overlay intercepts the tests'
        // clicks and everything times out. Pre-seed the "dismissed" flag so it never appears — except for the
        // dedicated promo test, which passes dismissDesktopPromo:false to exercise the real first-run behaviour.
        if (dismissDesktopPromo)
        {
            await context.AddInitScriptAsync("try { localStorage.setItem('sa.desktopClientNoticeDismissed', '1'); } catch (e) { }");
        }

        var page = await context.NewPageAsync();
        // 60s, not 30s: the Blazor WASM boot + OIDC login round-trip is slow on a 2-core GitHub-hosted runner
        // (the login helper's wait for the app bar timed out at 30s there, while passing locally on more cores).
        page.SetDefaultTimeout(60000);

        // …and the SAME reasoning for ASSERTIONS, which Playwright budgets SEPARATELY: SetDefaultTimeout covers
        // actions (click, fill, wait-for), while every `Expect(...)` runs on its own default of **5 s** unless
        // this is set. So the fix above only ever half-applied — a click got 60 s and the assertion that follows
        // it got 5, in the same test, on the same slow runner.
        //
        // That gap is a coin flip on anything asynchronous: an upload round-trip (presigned PUT → finalize →
        // classify → list refresh) legitimately needs more than 5 s on a 2-core hosted runner. Measured on the
        // runner that exposed this: WebEmailTests died at 11.7 s with its 5 s assertion expired, while a sibling
        // upload test PASSED at 14.2 s — the difference being how much of each test's time sat inside one
        // assertion. It reproduces on no developer machine, because there the same round-trip takes 6-7 s total.
        Assertions.SetDefaultExpectTimeout(30000);

        // DOMContentLoaded, not NetworkIdle: a Blazor WASM SPA keeps making background requests (WASM boot, the
        // OIDC silent-renew iframe), so the network never goes "idle" and GotoAsync times out — the source of an
        // intermittent login flake. Readiness is instead signalled by the explicit element waits below.
        await page.GotoAsync(app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByText("SimplArchive").First.WaitForAsync();

        await page.GetByText(LoginRegex()).First.ClickAsync();

        await page.WaitForSelectorAsync("input[name='Email'], input[type='email']");
        await page.FillAsync("input[name='Email'], input[type='email']", SelfHostedAppFixture.AdminEmail);
        await page.FillAsync("input[name='Password'], input[type='password']", SelfHostedAppFixture.AdminPassword);
        await page.ClickAsync("button[type='submit'], input[type='submit']");

        // Back in the SPA, authenticated — the logged-in user's DisplayName shows in the app bar (the email
        // that used to show there was removed, ADR "User profile photo").
        await page.Locator(".wb-appbar").GetByText(SelfHostedAppFixture.AdminDisplayName).WaitForAsync();
        return page;
    }

    [GeneratedRegex("^log ?in$", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRegex();

    // Obtains a demo-admin User access token by driving the real OAuth2 Authorization Code + PKCE flow over
    // HTTP (the same flow the SPA performs) — used to point the DesktopClient's api client at the self-hosted
    // API without a loopback-browser login.
    public static async Task<string> GetUserTokenAsync(string baseUrl, string? email = null, string? password = null)
    {
        email ??= SelfHostedAppFixture.AdminEmail;
        password ??= SelfHostedAppFixture.AdminPassword;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        const string redirectPath = "/authentication/login-callback";
        var authorize = "/connect/authorize?" + string.Join('&', new[]
        {
            "client_id=blazor-client", "response_type=code", $"redirect_uri={Uri.EscapeDataString(baseUrl + redirectPath)}",
            "scope=openid", $"code_challenge={challenge}", "code_challenge_method=S256", "state=x",
        });

        var loginPath = (await http.GetAsync(authorize)).Headers.Location!.ToString();
        var loginHtml = await http.GetStringAsync(loginPath);
        var antiforgery = Regex.Match(loginHtml, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var returnUrl = Regex.Match(loginPath, @"ReturnUrl=([^&]+)").Groups[1].Value;

        var login = await http.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = Uri.UnescapeDataString(returnUrl),
            ["__RequestVerificationToken"] = antiforgery,
        }));

        var next = login.Headers.Location!.ToString();
        string? code = null;
        for (var i = 0; i < 8 && code is null; i++)
        {
            var response = await http.GetAsync(next);
            if (response.Headers.Location is not { } location)
            {
                break;
            }

            var abs = location.IsAbsoluteUri ? location : new Uri(new Uri(baseUrl), location);
            var m = Regex.Match(abs.Query, @"[?&]code=([^&]+)");
            code = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
            next = abs.ToString();
        }

        var tokenResponse = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = baseUrl + redirectPath,
            ["client_id"] = "blazor-client",
            ["code_verifier"] = verifier,
        }));
        tokenResponse.EnsureSuccessStatusCode();
        var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
