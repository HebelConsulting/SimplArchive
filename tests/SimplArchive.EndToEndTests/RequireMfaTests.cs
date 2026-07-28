using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the tenant require-MFA policy (ADR "MFA require-policy +
// TOTP secret encryption"): when the tenant requires MFA, a not-yet-enrolled user's password login is diverted
// to inline enrolment (QR + code) on the shared login page, and only completes once they enrol — driven by the
// raw login-page HTTP with a cookie jar.
[Collection(E2ECollection.Name)]
public class RequireMfaTests
{
    private readonly E2EApiFactory _factory;

    public RequireMfaTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Require_mfa_forces_inline_enrolment_at_login()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"reqmfa-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "reqmfa-1234", "Require MFA");
        await SetRequireMfaAsync(tenantId, true);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        // Password step → the login page returns the ENROLMENT step (not a token/redirect).
        var loginPath = (await client.GetAsync(Authorize())).Headers.Location!.ToString();
        var enrollHtml = await PostFormAsync(client, loginPath, new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = "reqmfa-1234",
            ["ReturnUrl"] = ReturnUrlOf(loginPath),
            ["__RequestVerificationToken"] = TokenOf(await client.GetStringAsync(loginPath)),
        });

        Assert.Contains("Set up two-factor authentication", enrollHtml);
        var secret = Regex.Match(enrollHtml, @"<code>([A-Z2-7]+)</code>").Groups[1].Value;
        Assert.NotEmpty(secret);

        // Enrol step: submit a valid TOTP → the recovery-codes step.
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var recoveryHtml = await PostFormAsync(client, "/Account/Login?handler=Enroll", new()
        {
            ["EnrollTicket"] = HiddenOf(enrollHtml, "EnrollTicket"),
            ["ReturnUrl"] = HiddenOf(enrollHtml, "ReturnUrl"),
            ["Code"] = totp.ComputeTotp(),
            ["__RequestVerificationToken"] = TokenOf(enrollHtml),
        });

        Assert.Contains("Save your recovery codes", recoveryHtml);

        // MFA is now enrolled for the user.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            Assert.NotNull(user.MfaEnabledAt);
            Assert.False(string.IsNullOrEmpty(user.TotpSecret));
        }

        // Continue → sign-in completes (302 back through the authorize flow, ultimately an auth code).
        var continueResponse = await PostFormRawAsync(client, "/Account/Login?handler=Continue", new()
        {
            ["ContinueTicket"] = HiddenOf(recoveryHtml, "ContinueTicket"),
            ["ReturnUrl"] = HiddenOf(recoveryHtml, "ReturnUrl"),
            ["__RequestVerificationToken"] = TokenOf(recoveryHtml),
        });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, continueResponse.StatusCode);

        // Clean up so require-MFA doesn't affect other tests sharing this factory's tenant set.
        await SetRequireMfaAsync(tenantId, false);
    }

    private async Task SetRequireMfaAsync(Guid tenantId, bool value)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.RequireMfa = value;
        await db.SaveChangesAsync();
    }

    private static string Authorize()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return "/connect/authorize?" + string.Join('&',
            "client_id=blazor-client", "response_type=code",
            $"redirect_uri={Uri.EscapeDataString("http://localhost/authentication/login-callback")}",
            "scope=openid", $"code_challenge={challenge}", "code_challenge_method=S256", "state=x");
    }

    private static async Task<string> PostFormAsync(HttpClient client, string url, Dictionary<string, string> form) =>
        await (await client.PostAsync(url, new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();

    private static Task<HttpResponseMessage> PostFormRawAsync(HttpClient client, string url, Dictionary<string, string> form) =>
        client.PostAsync(url, new FormUrlEncodedContent(form));

    private static string TokenOf(string html) => Regex.Match(html, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
    private static string HiddenOf(string html, string name) => Regex.Match(html, $@"name=""{name}""[^>]*value=""([^""]*)""").Groups[1].Value;
    private static string ReturnUrlOf(string loginPath) => QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
