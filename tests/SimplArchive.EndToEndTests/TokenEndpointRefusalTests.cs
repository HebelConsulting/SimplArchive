using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.EndToEndTests;

// What /connect/token does when it says NO (#595, ADR 0626).
//
// The endpoint had no logger at all: three distinct invalid_client refusals and every impersonation refusal
// produced nothing anywhere. That is worse here than elsewhere, because the RESPONSE is deliberately opaque —
// an unknown client, a wrong secret and a deactivated account are all `invalid_client`, so a prober learns
// nothing about which accounts exist. Correct, and exactly why the server has to record it: otherwise a
// refusal is invisible from both sides, and neither an operator diagnosing a broken integration nor a SIEM
// watching for a brute-force run has anything to work with.
//
// These tests pin the CONTRACT the logging exists to support: that the refusals happen, and that they stay
// indistinguishable to the caller. The log line itself is asserted where it can be — a test that scrapes log
// output would pin the message text rather than the behaviour.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class TokenEndpointRefusalTests
{
    private readonly E2EApiFactory _factory;

    public TokenEndpointRefusalTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpResponseMessage Response, string Error)> RequestAsync(string clientId, string secret)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));

        var body = await response.Content.ReadAsStringAsync();
        var error = body.Length > 0 && JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e)
            ? e.GetString() ?? string.Empty
            : string.Empty;

        return (response, error);
    }

    [Fact]
    public async Task An_unknown_client_and_a_wrong_secret_are_indistinguishable()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        // A real client id with the wrong secret, and a client id that does not exist at all. The two must
        // answer identically — the moment they differ, the endpoint becomes an oracle for enumerating accounts.
        var wrongSecret = await RequestAsync(clientId, $"definitely-not-{secret}");
        var unknownClient = await RequestAsync($"no-such-client-{Guid.NewGuid():N}", secret);

        // Both are refused by OpenIddict's own client authentication, BEFORE our controller runs — 401 with a
        // bare invalid_client. Identical in status and body, which is the property that matters: without the
        // secret, nothing here distinguishes a real client id from one that was never issued.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownClient.Response.StatusCode);
        Assert.Equal("invalid_client", unknownClient.Error);
        Assert.Equal(wrongSecret.Error, unknownClient.Error);
    }

    [Fact]
    public async Task A_deactivated_service_account_is_refused_the_same_way()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        // It works first — otherwise the refusal below would prove nothing about deactivation.
        Assert.False(string.IsNullOrEmpty(await _factory.GetTokenAsync(clientId, secret)));

        // Deactivate it directly — the same scope-off-the-factory pattern the other E2E tests use to arrange
        // state the API has no endpoint for.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var account = await db.ServiceAccounts.IgnoreQueryFilters()
                .SingleAsync(a => a.OpenIddictApplicationClientId == clientId);
            account.IsActive = false;
            await db.SaveChangesAsync();
        }

        var (response, error) = await RequestAsync(clientId, secret);

        // 400 here, not the 401 an unknown client gets — because this request AUTHENTICATED successfully and
        // was then refused by us for being deactivated. The difference is only observable to a caller who
        // already holds the correct secret, so it is not an enumeration oracle; it is simply the one refusal
        // our own code owns, which is exactly why it needed a log line of its own.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_client", error);

        // No detail beyond the code: the body must not say WHY, or explain which tenant it belonged to.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("deactivat", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenantId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refusal_never_echoes_the_secret_it_was_given()
    {
        // The response body is one place a credential must never appear — it is logged by proxies, kept in
        // browser history, and pasted into issue trackers by people debugging.
        const string secret = "s3cr3t-that-must-not-come-back";
        var (response, _) = await RequestAsync($"no-such-client-{Guid.NewGuid():N}", secret);

        Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
