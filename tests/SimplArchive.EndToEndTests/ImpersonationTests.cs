using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres (ADR "User impersonation"): a CanImpersonate admin exchanges their
// token (RFC 8693) for one representing a non-admin target user; whoami then shows the target + the acting admin.
// A caller without CanImpersonate is refused, and an admin target can't be impersonated.
[Collection(E2ECollection.Name)]
public class ImpersonationTests
{
    private readonly E2EApiFactory _factory;

    public ImpersonationTests(E2EApiFactory factory) => _factory = factory;

    private static async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string actorToken, Guid targetUserId) =>
        await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = "blazor-client",
            ["subject_token"] = actorToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["requested_subject"] = targetUserId.ToString(),
        }));

    [Fact]
    public async Task CanImpersonate_admin_acts_as_a_non_admin_user()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var actorEmail = $"actor-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, actorEmail, "actor-1234", "Actor Admin");
        await _factory.GrantCanImpersonateAsync(actorEmail);

        var targetEmail = $"target-{Guid.NewGuid():N}@e2e.local";
        var targetId = await _factory.SeedUserAsync(tenantId, targetEmail, "target-1234", "Target User");

        var actorToken = await _factory.GetUserTokenAsync(actorEmail, "actor-1234");
        using var http = _factory.CreateClient();

        // Exchange the actor's token for an impersonation token representing the target.
        var response = await ExchangeAsync(http, actorToken, targetId);
        if (!response.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        var impersonationToken = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        // whoami with the impersonation token resolves the target user + names the acting admin.
        using var impersonated = _factory.CreateAuthedClient(impersonationToken);
        var whoami = await TestJson.Get(impersonated, "/api/diagnostics/whoami");
        Assert.Equal(targetId, whoami.GetProperty("userId").GetGuid());
        Assert.Equal(tenantId, whoami.GetProperty("tenantId").GetGuid());
        Assert.Equal("Actor Admin", whoami.GetProperty("impersonatedBy").GetString());
    }

    [Fact]
    public async Task Impersonation_is_refused_without_the_right_or_for_an_admin_target()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        // A user WITHOUT CanImpersonate.
        var plainEmail = $"plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, plainEmail, "plain-1234", "Plain User");
        var targetId = await _factory.SeedUserAsync(tenantId, $"t-{Guid.NewGuid():N}@e2e.local", "t-1234", "Target");
        using var http = _factory.CreateClient();

        var refused = await ExchangeAsync(http, await _factory.GetUserTokenAsync(plainEmail, "plain-1234"), targetId);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); // access_denied

        // A CanImpersonate admin cannot impersonate a tenant-admin target.
        var actorEmail = $"actor-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, actorEmail, "actor-1234", "Actor");
        await _factory.GrantCanImpersonateAsync(actorEmail);
        var adminTargetEmail = $"admint-{Guid.NewGuid():N}@e2e.local";
        var adminTargetId = await _factory.SeedUserAsync(tenantId, adminTargetEmail, "admint-1234", "Admin Target");
        await _factory.GrantTenantAdminAsync(adminTargetEmail);

        var refusedAdmin = await ExchangeAsync(http, await _factory.GetUserTokenAsync(actorEmail, "actor-1234"), adminTargetId);
        Assert.Equal(HttpStatusCode.BadRequest, refusedAdmin.StatusCode); // invalid_grant
    }
}
