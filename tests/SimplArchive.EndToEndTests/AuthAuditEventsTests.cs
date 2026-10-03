using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.Controllers;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// A refused credential and a deliberate sign-out reach the AUDIT TRAIL, not only the log (#847, A07/A09).
//
// WHY THE TRAIL AND NOT THE LOG. A failed sign-in was already a Serilog Warning, which is what a SIEM needs
// to count attempts. It is not what an investigation needs, because the question an investigation asks is
// "what happened to THIS account, in order, beside everything else that happened to it" — and the rest of
// that story is in the audit trail, with the one event that matters most missing from it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AuthAuditEventsTests(E2EApiFactory factory)
{
    private async Task<List<string>> ActionsForAsync(Guid tenantId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        return await db.AuditEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.ActorId == userId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Action + (e.Details == null ? "" : $":{e.Details}"))
            .ToListAsync();
    }

    /// <summary>Drives the real login page to the point of posting <paramref name="password"/>.</summary>
    private async Task<HttpClient> PostCredentialsAsync(string email, string password)
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var authorize = "/connect/authorize?client_id=blazor-client&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost/authentication/login-callback")
            + "&scope=openid&code_challenge=x&code_challenge_method=plain&state=x";
        var loginPath = (await client.GetAsync(authorize)).Headers.Location!.ToString();
        var html = await client.GetStringAsync(loginPath);
        var antiforgery = Regex.Match(html, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();

        await client.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgery,
        }));

        return client;
    }

    [Fact]
    public async Task A_refused_password_is_filed_in_the_users_own_trail()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"wrongpass-{Guid.NewGuid():N}@e2e.local";
        var userId = await factory.SeedUserAsync(tenantId, email, "correct-horse-1234", "Wrong Pass");

        using var _ = await PostCredentialsAsync(email, "not-the-password");

        var actions = await ActionsForAsync(tenantId, userId);

        // The DETAIL names which factor was refused, because a second-factor failure means somebody already
        // had the password — a different event to whoever reads this afterwards.
        Assert.Contains($"{AuditActions.LoginFailed}:password", actions);

        // …and the attempt did NOT produce a sign-in event. Asserted because the pair is what makes the trail
        // readable: a refusal recorded beside a success nobody earned would be worse than no record at all.
        Assert.DoesNotContain(AuditActions.LoggedIn, actions);
    }

    [Fact]
    public async Task Signing_out_is_recorded_against_the_user_who_signed_out()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"signsout-{Guid.NewGuid():N}@e2e.local";
        var userId = await factory.SeedUserAsync(tenantId, email, "correct-horse-1234", "Signs Out");

        using var client = await PostCredentialsAsync(email, "correct-horse-1234");
        await client.GetAsync("/Account/Logout");

        var actions = await ActionsForAsync(tenantId, userId);

        // BOTH halves, in order. The sign-out alone would say nothing about the session's extent; the pair is
        // what lets a reader answer "was anyone still signed in when this happened".
        Assert.Equal(
            [AuditActions.LoggedIn, AuditActions.LoggedOut],
            actions.Where(a => a.StartsWith("Auth.", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public async Task An_anonymous_hit_on_the_sign_out_page_records_nothing_and_still_answers()
    {
        // Ordinary traffic: a bookmark, a second tab, a client clearing up after a session that already ended.
        // Signing out is idempotent, so the page must still answer — it is only the audit event that is
        // skipped, because there is no actor to attribute one to.
        using var anonymous = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await anonymous.GetAsync("/Account/Logout");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task An_attempt_against_an_address_that_matches_no_user_is_not_filed_anywhere()
    {
        // THE BOUNDARY, asserted so it cannot be mistaken for an oversight later. An AuditEvent is
        // tenant-scoped and an unknown address has no tenant, so there is no honest place to file it — those
        // attempts stay a Serilog Warning, which is where a spray against non-existent accounts is visible.
        // Do not let this become "failed logins are audited" without the qualification (#1092).
        var before = await TotalAuditEventsAsync();

        using var _ = await PostCredentialsAsync($"nobody-{Guid.NewGuid():N}@e2e.local", "whatever");

        Assert.Equal(before, await TotalAuditEventsAsync());
    }

    private async Task<int> TotalAuditEventsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        // Across EVERY tenant, which is the only assertion that means anything here: the claim is that the
        // attempt lands nowhere, and counting one tenant would pass while it was filed in another.
        return await db.AuditEvents.IgnoreQueryFilters().CountAsync();
    }

    [Fact]
    public async Task A_lockout_is_filed_ONCE_however_many_attempts_follow_it()
    {
        // #1569. The event that matters is the moment the account stopped answering — "a lockout with no audit
        // trail cannot be investigated afterwards" is the register's own phrasing.
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"lockout-{Guid.NewGuid():N}@e2e.local";
        var userId = await factory.SeedUserAsync(tenantId, email, "correct-horse-1234", "Locked Out");

        // Guess until the wall goes up, then keep guessing well past it. THE SECOND HALF IS THE TEST: while a
        // block holds every attempt is refused, so a naive implementation recording per refusal would write a
        // pile of identical events and bury the trail somebody is trying to read.
        for (var attempt = 0; attempt < 14; attempt++)
        {
            using var _ = await PostCredentialsAsync(email, $"wrong-{attempt}");
        }

        var actions = await ActionsForAsync(tenantId, userId);

        Assert.Equal(1, actions.Count(a => a.StartsWith(AuditActions.LockedOut, StringComparison.Ordinal)));

        // …and the refusals that led to it are all there, which is what makes the one event placeable in time.
        Assert.True(
            actions.Count(a => a.StartsWith(AuditActions.LoginFailed, StringComparison.Ordinal)) > 1,
            "The refused attempts before the block should each be recorded — the lockout alone says when the "
            + $"door shut and nothing about the knocking. Got: {string.Join(", ", actions)}");
    }
}
