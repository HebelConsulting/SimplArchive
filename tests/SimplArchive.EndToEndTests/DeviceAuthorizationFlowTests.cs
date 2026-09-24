using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace SimplArchive.EndToEndTests;

// The device authorization grant end to end (RFC 8628, ADR 0823): saconsole asks for a code on a machine with
// no browser, a PERSON answers at /connect/verify, and only then does the waiting tool get a token.
//
// It is driven the whole way — device endpoint, the pending poll, the browser redirect to sign in, the real
// login POST, the approval form, the token, and the token USED — because every cheaper check passes while the
// thing is broken. A 200 from the verify page proves it rendered, not that approving it grants anything; the
// only evidence that the grant works is a token that the API then accepts as the approving user.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class DeviceAuthorizationFlowTests
{
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly E2EApiFactory _factory;

    public DeviceAuthorizationFlowTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Approving_a_device_code_issues_a_token_that_acts_as_the_approving_user()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"device-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "dev-1234", "Device Admin");

        using var anonymous = _factory.CreateClient();
        var (deviceCode, userCode) = await StartDeviceFlowAsync(anonymous);

        // Before anybody has answered, the tool's poll must say "keep waiting" — not "no" and not a token.
        var pending = await RedeemAsync(anonymous, deviceCode);
        Assert.Equal("authorization_pending", pending.GetProperty("error").GetString());

        var browser = Browser();

        // The approval page never renders to an unauthenticated visitor: it sends them to sign in first,
        // carrying the code so they come back to the same request.
        var challenge = await browser.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(userCode)}");
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        Assert.StartsWith("/Account/Login?ReturnUrl=", challenge.Headers.Location!.ToString(), StringComparison.Ordinal);

        var approval = await SignInAndReturnAsync(browser, challenge.Headers.Location!.ToString(), email, "dev-1234");
        var html = await approval.Content.ReadAsStringAsync();

        // What the page names: the code (so it can be compared with the terminal), the client, the account.
        Assert.Contains(userCode, html, StringComparison.Ordinal);
        Assert.Contains("saconsole", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(email, html, StringComparison.OrdinalIgnoreCase);

        var approved = await SubmitAsync(browser, html, "Approve", userCode);
        Assert.Equal(HttpStatusCode.Found, approved.StatusCode);
        Assert.Contains("outcome=approved", approved.Headers.Location!.ToString(), StringComparison.Ordinal);

        // The terminal page says the browser is finished, and offers nothing onward.
        var result = await browser.GetAsync(approved.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        // Now the half that a rendering check cannot see: the waiting tool gets a real token...
        var token = await RedeemAsync(anonymous, deviceCode);
        var accessToken = token.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrEmpty(accessToken));

        // ...and the API accepts it AS the person who approved it, which is the whole point of the grant.
        using var tool = _factory.CreateAuthedClient(accessToken!);
        var whoami = await TestJson.Get(tool, "/api/diagnostics/whoami");
        Assert.Equal(userId, whoami.GetProperty("userId").GetGuid());
        Assert.Equal(tenantId, whoami.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task Refusing_answers_access_denied_so_the_waiting_tool_stops_rather_than_polling_on()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"refuse-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "ref-1234", "Refusing Admin");

        using var anonymous = _factory.CreateClient();
        var (deviceCode, userCode) = await StartDeviceFlowAsync(anonymous);

        var browser = Browser();
        var challenge = await browser.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(userCode)}");
        var approval = await SignInAndReturnAsync(browser, challenge.Headers.Location!.ToString(), email, "ref-1234");

        var refused = await SubmitAsync(browser, await approval.Content.ReadAsStringAsync(), "Refuse", userCode);
        Assert.Equal(HttpStatusCode.Found, refused.StatusCode);
        Assert.Contains("outcome=refused", refused.Headers.Location!.ToString(), StringComparison.Ordinal);

        // A refusal that only changed the page would leave the tool polling until the code expired. The answer
        // has to reach the token endpoint, and it has to be a NO rather than a "still waiting".
        var answer = await RedeemAsync(anonymous, deviceCode);
        Assert.Equal("access_denied", answer.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_existing_session_must_authenticate_again_before_it_can_approve()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"stale-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "stl-1234", "Signed-in Admin");

        using var anonymous = _factory.CreateClient();
        var (_, firstCode) = await StartDeviceFlowAsync(anonymous);
        var (_, secondCode) = await StartDeviceFlowAsync(anonymous);

        var browser = Browser();

        // Establish a real, valid session by completing one approval journey end to end.
        var challenge = await browser.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(firstCode)}");
        var approval = await SignInAndReturnAsync(browser, challenge.Headers.Location!.ToString(), email, "stl-1234");
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        // That session is seconds old and entirely genuine — and it still does not buy a second approval. This
        // is the decision the page exists to enforce: a link arriving at an already-signed-in browser cannot
        // be approved by one click, because the grant hands a token to a machine somebody else may be at.
        var second = await browser.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(secondCode)}");
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.StartsWith("/Account/Login?ReturnUrl=", second.Headers.Location!.ToString(), StringComparison.Ordinal);

        // A forged marker buys nothing either: it is data-protected, so anything this application did not
        // mint simply does not unprotect, and the request is treated exactly like one that carried none.
        var forged = await browser.GetAsync(
            $"/connect/verify?user_code={Uri.EscapeDataString(secondCode)}&c=not-a-real-marker");
        Assert.Equal(HttpStatusCode.Found, forged.StatusCode);
        Assert.StartsWith("/Account/Login?ReturnUrl=", forged.Headers.Location!.ToString(), StringComparison.Ordinal);

        // What the marker does NOT claim, said here so nobody reads more into it later: it proves this browser
        // authenticated after this page asked it to, not that the code came from the person answering. A
        // marker minted elsewhere and mailed on is bounded by its ten-minute life, and the defence that
        // actually covers that case is the code echoed on the page for comparison (RFC 8628 §5.4).
    }

    [Fact]
    public async Task An_unusable_code_gets_one_message_that_does_not_say_which_fault_it_was()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"badcode-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "bad-1234", "Curious Admin");

        var browser = Browser();
        var challenge = await browser.GetAsync("/connect/verify?user_code=ZZZZ-ZZZZ");
        var page = await SignInAndReturnAsync(browser, challenge.Headers.Location!.ToString(), email, "bad-1234");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // The refusal must not distinguish "never existed" from "expired" — telling them apart would confirm
        // to a guesser which codes are real, and the remedy is the same either way.
        Assert.DoesNotContain("expired", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown", html, StringComparison.OrdinalIgnoreCase);

        // Matched against the RESOURCE, encoded the way Razor encodes it. Two traps, both met here: the
        // English prose contains an apostrophe, so a literal "can't be used" never matches; and
        // WebUtility.HtmlEncode spells that apostrophe &#39; while Razor's HtmlEncoder spells it &#x27; —
        // same character, different escape, and the assertion fails against a page that is perfectly correct.
        Assert.Contains(
            HtmlEncoder.Default.Encode(SimplArchive.Localization.Strings.Get("DeviceUnusableTitle")),
            html, StringComparison.Ordinal);

        // And the half that matters: posting the approval by hand must not grant anything either. The page
        // refusing to RENDER an approval is cosmetic if the handler behind it would still sign one in, and
        // the first version of this page did exactly that — OpenIddict answers "authenticated" at this
        // endpoint even when no code resolved, so `result.Succeeded` accepted "ZZZZ-ZZZZ" and drew a complete
        // approval card for it, Approve button and all.
        var forged = await SubmitAsync(browser, html, "Code", "ZZZZ-ZZZZ", handlerPosted: "Approve");
        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        Assert.DoesNotContain("outcome=approved", forged.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    private HttpClient Browser() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<(string DeviceCode, string UserCode)> StartDeviceFlowAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/connect/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "saconsole",
            ["scope"] = "openid",
        }));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        var json = JsonDocument.Parse(body).RootElement;
        return (json.GetProperty("device_code").GetString()!, json.GetProperty("user_code").GetString()!);
    }

    private static async Task<JsonElement> RedeemAsync(HttpClient client, string deviceCode)
    {
        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = DeviceCodeGrant,
            ["device_code"] = deviceCode,
            ["client_id"] = "saconsole",
        }));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // Drives the real sign-in page and follows the redirect chain back to whatever asked for it.
    private static async Task<HttpResponseMessage> SignInAndReturnAsync(
        HttpClient browser, string loginPath, string email, string password)
    {
        var loginHtml = await browser.GetStringAsync(loginPath);
        var antiforgery = Antiforgery(loginHtml);
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();

        var login = await browser.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgery,
        }));

        var next = login.Headers.Location!.ToString();
        var response = await browser.GetAsync(next);

        for (var i = 0; i < 4 && response.Headers.Location is { } location; i++)
        {
            response = await browser.GetAsync(location.ToString());
        }

        return response;
    }

    /// <summary>
    /// Posts the form that carries <paramref name="handler"/>, optionally to a DIFFERENT handler — which is
    /// how a forged approval is built: the refusal page offers only a code form, so its antiforgery token is
    /// the one an attacker would have to hand.
    /// </summary>
    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient browser, string html, string handler, string userCode, string? handlerPosted = null)
    {
        // The antiforgery token of the form carrying this handler, not simply the first on the page: the
        // approval card has two forms, and posting Approve with Refuse's token is a 400 that would read as a
        // broken grant.
        var form = Regex.Match(html, $@"<form[^>]*handler={handler}[^>]*>.*?</form>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(form.Success, $"The {handler} form is not on the page.");

        return await browser.PostAsync($"/connect/verify?handler={handlerPosted ?? handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user_code"] = userCode,
                // The page echoes its sign-out marker into the form; posting without it would be re-challenged.
                ["c"] = Regex.Match(form.Value, @"name=""c""[^>]*value=""([^""]*)""").Groups[1].Value,
                ["__RequestVerificationToken"] = Antiforgery(form.Value),
            }));
    }

    private static string Antiforgery(string html)
    {
        var token = Regex.Match(html, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "No antiforgery token in the rendered form.");
        return token;
    }
}
