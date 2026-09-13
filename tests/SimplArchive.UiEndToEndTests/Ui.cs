using System.Diagnostics;
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

    /// <summary>
    /// Opens the tree context menu's "New" submenu and waits for <paramref name="expected"/> to appear in it.
    /// </summary>
    /// <remarks>
    /// The creates a folder offers are nested under one "New" entry (#673), matching the desktop (ADR 0511),
    /// so every test that used to click "New subfolder" now opens this first.
    /// <para>
    /// Hover THEN click, rather than one or the other: MudBlazor's submenus open on pointer-enter, but the
    /// activation is configurable and a headless hover does not always settle into the popover on a loaded
    /// runner. Trying both costs one click on the item that is already open — which MudBlazor treats as a
    /// no-op — and saves a whole suite run spent discovering which of the two it was.
    /// </para>
    /// </remarks>
    public static async Task<ILocator> OpenNewSubmenuAsync(IPage page, string expected)
    {
        var items = page.Locator(".mud-menu-item");
        var newItem = items.Filter(new() { HasText = "New" }).First;
        await Assertions.Expect(newItem).ToBeVisibleAsync();
        await newItem.HoverAsync();

        var entry = items.Filter(new() { HasText = expected }).First;
        try
        {
            await Assertions.Expect(entry).ToBeVisibleAsync(new() { Timeout = 2000 });
        }
        catch (PlaywrightException)
        {
            await newItem.ClickAsync();
            await Assertions.Expect(entry).ToBeVisibleAsync();
        }

        return entry;
    }

    /// <summary>Log out through the account menu and wait for BOTH halves of it to finish.</summary>
    /// <remarks>
    /// Logging out is two navigations, not one: RemoteAuthenticatorView clears this client's tokens and renders
    /// "You are logged out", and its callback then sends the whole page to /Account/Logout to end the SERVER
    /// session. A test that acts the instant that text appears is standing in the middle of the second one —
    /// script evaluated there dies with "Execution context was destroyed", and a navigation issued there ABORTS
    /// the sign-out request, leaving the cookie alive so the next login is silent. Both shapes turned main red
    /// the day the server half landed, and neither says "race" when you read the failure.
    /// <para>
    /// The wait is armed BEFORE the click, so the request cannot slip past between the two.
    /// </para>
    /// </remarks>
    public static async Task LogOutAsync(IPage page)
    {
        var serverSignOut = page.WaitForRequestFinishedAsync(new()
        {
            Predicate = r => r.Url.Contains("/Account/Logout", StringComparison.OrdinalIgnoreCase),
        });

        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Log out").First.ClickAsync();
        await Assertions.Expect(page.GetByText("You are logged out")).ToBeVisibleAsync();

        await serverSignOut;

        // Logout is finished when the page has STOPPED NAVIGATING — not when the URL looks right (#1140).
        //
        // This line has had four attempts because each of the first three answered the wrong question. The
        // sequence, finally OBSERVED by recording every FrameNavigated with a timestamp rather than inferred
        // from a stack trace, is:
        //
        //     251 ms  main  /authentication/logged-out
        //     257 ms  main  /authentication/logged-out     <- a SECOND navigation, to the same URL
        //     689 ms  LogOutAsync returned
        //
        // TWO main-frame navigations to the same address, milliseconds apart. A URL predicate is satisfied by
        // the first and cannot distinguish them, so the helper could return inside the gap; the caller's
        // GotoAsync then started there and Playwright aborted it with "interrupted by another navigation",
        // which reads as the caller's bug. On this machine the gap is ~6 ms and it almost never bites; on a
        // loaded two-core runner it is wide enough to lose, which is why it was CI-only and why three fixes
        // reasoned from a single trace each closed a window that was not the one open.
        //
        // So: wait for the URL, then wait for QUIET. Not a sleep — it returns as soon as the main frame has
        // been still for the settle window, and it is bounded so a future change cannot turn it into a hang.
        // Counting navigations instead ("wait for the second") would encode today's count as a law.
        await page.WaitForURLAsync(
            url => url.Contains("logged-out", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await WaitForMainFrameToSettleAsync(page);
    }

    /// <summary>How long the main frame must be still before a navigation sequence counts as finished.</summary>
    /// <remarks>
    /// Generous against the ~6 ms observed locally, because the whole point is the loaded runner where the
    /// gap is wider. It is a QUIET window rather than a delay: a settled page costs the window once, and
    /// nothing waits the full budget unless the page really is still navigating.
    /// </remarks>
    private static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(15);

    /// <summary>Returns once the page's MAIN frame has not navigated for <see cref="SettleQuiet"/>.</summary>
    /// <remarks>
    /// Deliberately main-frame only. A Blazor WASM app's OIDC silent-renew iframe navigates to
    /// <c>about:blank</c> on its own schedule, so counting subframes would be waiting for a silence this app
    /// never reaches — which is the same mistake NetworkIdle made here (#1081), one level down.
    /// </remarks>
    private static async Task WaitForMainFrameToSettleAsync(IPage page)
    {
        var sinceLastNavigation = Stopwatch.StartNew();
        void OnNavigated(object? sender, IFrame frame)
        {
            if (frame == page.MainFrame)
            {
                sinceLastNavigation.Restart();
            }
        }

        page.FrameNavigated += OnNavigated;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (sinceLastNavigation.Elapsed < SettleQuiet && deadline.Elapsed < SettleBudget)
            {
                await Task.Delay(25);
            }
        }
        finally
        {
            page.FrameNavigated -= OnNavigated;
        }
    }

    /// <summary>
    /// Returns once no <c>/api/documents/</c> request has been in flight for a short quiet window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For tests that INJECT a failure into a detail load. A pane loads asynchronously and a new selection
    /// supersedes an old one (ADR 0559), so at any moment a previous subject's requests may still be in the
    /// air — and a test that aborts "the first matching call" hits whichever load happens to be running.
    /// </para>
    /// <para>
    /// That is not hypothetical: aborting a FOLDER's superseded load produced no error message at all, and
    /// correctly so — <c>DetailLoader</c> discards a superseded result rather than painting a stale subject's
    /// failure (ADR 0559). The test then waited 30 s for a message the client had no reason to show, about 1
    /// run in 5, and the product was right every time.
    /// </para>
    /// <para>
    /// NOT NetworkIdle, which this app never reaches — a Blazor WASM SPA's OIDC silent-renew iframe keeps
    /// talking (#1081). Scoped to the document API, which is the only traffic that can carry the load under
    /// test, and bounded so it degrades to "carry on" rather than hanging.
    /// </para>
    /// </remarks>
    public static async Task WaitForDocumentApiQuietAsync(IPage page, int quietMs = 400, int budgetMs = 15000)
    {
        var inFlight = 0;
        var sinceLastActivity = Stopwatch.StartNew();

        void Started(object? sender, IRequest request)
        {
            if (request.Url.Contains("/api/documents/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref inFlight);
                sinceLastActivity.Restart();
            }
        }

        void Ended(object? sender, IRequest request)
        {
            if (request.Url.Contains("/api/documents/", StringComparison.Ordinal))
            {
                Interlocked.Decrement(ref inFlight);
                sinceLastActivity.Restart();
            }
        }

        page.Request += Started;
        page.RequestFinished += Ended;
        page.RequestFailed += Ended;
        try
        {
            var deadline = Stopwatch.StartNew();
            while ((Volatile.Read(ref inFlight) > 0 || sinceLastActivity.ElapsedMilliseconds < quietMs)
                   && deadline.ElapsedMilliseconds < budgetMs)
            {
                await Task.Delay(25);
            }
        }
        finally
        {
            page.Request -= Started;
            page.RequestFinished -= Ended;
            page.RequestFailed -= Ended;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
