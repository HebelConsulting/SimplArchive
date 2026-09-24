using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.EndToEndTests;

// saconsole's own device-flow client against a real server (ADR 0823), exercising THE CLI's code rather than
// a re-implementation of it.
//
// That distinction is the point. `DeviceAuthorizationFlowTests` drives the protocol with hand-written HTTP and
// proves the SERVER is right; it would pass just as happily if saconsole's polling loop ignored `interval`,
// mistook `slow_down` for a failure, or retried after a person had said no. A replay built by analogy replays
// its author's assumptions — so this one calls DeviceFlow, the type the shipped tool uses.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SaConsoleDeviceLoginTests
{
    private readonly E2EApiFactory _factory;

    public SaConsoleDeviceLoginTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_cli_requests_a_code_waits_for_approval_and_receives_a_usable_token()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"cli-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "cli-1234", "CLI Admin");

        using var transport = _factory.CreateClient();
        var flow = new DeviceFlow(transport);

        // 1. The tool asks for a code, exactly as `saconsole login` does.
        var authorization = await flow.RequestAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(authorization.UserCode));
        Assert.False(string.IsNullOrEmpty(authorization.DeviceCode));
        Assert.Contains("connect/verify", authorization.VerificationUri, StringComparison.Ordinal);

        // RFC 8628 §3.2 makes `interval` optional and specifies 5 seconds when absent. A zero here would mean
        // the shipped tool polls in a tight loop against a live installation.
        Assert.True(authorization.Interval > TimeSpan.Zero, "the client must never poll with no interval");
        Assert.True(authorization.ExpiresAt > DateTimeOffset.UtcNow);

        // 2. A person approves, in a browser, while the tool polls — so the poll really does start against an
        //    unapproved code and has to wait, which is the behaviour under test.
        var approval = Task.Run(async () =>
        {
            await ApproveAsync(authorization.UserCode, email, "cli-1234");
        });

        var token = await flow.PollAsync(authorization, _ => { }, CancellationToken.None);
        await approval;

        Assert.False(string.IsNullOrEmpty(token));

        // 3. The token the CLI ended up with is accepted AS that user — the only check that proves the whole
        //    chain rather than the protocol's shape.
        using var tool = _factory.CreateAuthedClient(token);
        var whoami = await TestJson.Get(tool, "/api/diagnostics/whoami");
        Assert.Equal(userId, whoami.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task A_refusal_reaches_the_cli_as_a_message_rather_than_a_retry()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"clino-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "cli-1234", "Refusing CLI Admin");

        using var transport = _factory.CreateClient();
        var flow = new DeviceFlow(transport);
        var authorization = await flow.RequestAsync(CancellationToken.None);

        var refusal = Task.Run(async () =>
        {
            await ApproveAsync(authorization.UserCode, email, "cli-1234", handler: "Refuse");
        });

        // access_denied must STOP the loop. A client that treated it as another "keep waiting" would poll a
        // dead code until it expired, and pester somebody who had already answered.
        var error = await Assert.ThrowsAsync<CliException>(
            () => flow.PollAsync(authorization, _ => { }, CancellationToken.None));
        await refusal;

        Assert.Contains("refused", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drives the real browser journey: sign in, then approve or refuse the code.</summary>
    private async Task ApproveAsync(string userCode, string email, string password, string handler = "Approve")
    {
        var browser = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var challenge = await browser.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(userCode)}");
        var loginPath = challenge.Headers.Location!.ToString();

        var loginHtml = await browser.GetStringAsync(loginPath);
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();

        var login = await browser.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = Token(loginHtml),
        }));

        var response = await browser.GetAsync(login.Headers.Location!.ToString());
        for (var i = 0; i < 4 && response.Headers.Location is { } next; i++)
        {
            response = await browser.GetAsync(next.ToString());
        }

        var page = await response.Content.ReadAsStringAsync();
        var form = Regex.Match(page, $@"<form[^>]*handler={handler}[^>]*>.*?</form>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(form.Success, $"the {handler} form is not on the approval page");

        using var answer = await browser.PostAsync($"/connect/verify?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user_code"] = userCode,
                ["c"] = Regex.Match(form.Value, @"name=""c""[^>]*value=""([^""]*)""").Groups[1].Value,
                ["__RequestVerificationToken"] = Token(form.Value),
            }));

        Assert.Equal(HttpStatusCode.Found, answer.StatusCode);
    }

    private static string Token(string html) =>
        Regex.Match(html, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
}
