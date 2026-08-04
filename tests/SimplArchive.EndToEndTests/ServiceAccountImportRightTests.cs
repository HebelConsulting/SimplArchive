using System.Net;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API for ADR 0523: CanImport/CanExport are grantable on a service account at
// creation, under the same escalation cap as the three management rights — so a migration porter
// (a client-credentials batch tool) can be issued a CanImport credential. A caller lacking the right can't
// hand it out. Proves the created account's client-credentials token can actually backdate a version.
[Collection(E2ECollection.Name)]
public class ServiceAccountImportRightTests
{
    private readonly E2EApiFactory _factory;

    public ServiceAccountImportRightTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_grants_CanImport_capped_by_caller_and_the_new_account_can_backdate()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);

        const string password = "sair-1234";
        // The admin caller: can manage service accounts AND holds CanImport, so it may hand CanImport out. It also
        // holds CanManageRepositories so it can delegate that to the porter account (which must create the target repo).
        var adminEmail = $"sa-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "SA Admin", canImport: true, canManageServiceAccounts: true, canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        // Grant CanImport (+ CanManageRepositories, like a real migration credential) on a new service account →
        // 201, and the response echoes the import right.
        var created = await TestJson.Post(admin, "/api/service-accounts", new { name = $"porter-{Guid.NewGuid():N}", canImport = true, canManageRepositories = true });
        Assert.True(created.GetProperty("canImport").GetBoolean());
        var newClientId = created.GetProperty("clientId").GetString()!;
        var newSecret = created.GetProperty("clientSecret").GetString()!;

        // The new account's own client-credentials token can backdate a version (CanImport-gated, ADR 0520) — the
        // presigned upload key lands under the 2003 filing-year bucket, which a caller lacking CanImport is refused.
        using var porter = _factory.CreateAuthedClient(await _factory.GetTokenAsync(newClientId, newSecret));
        var repoId = (await TestJson.Post(porter, "/api/repositories", new { name = $"SAIR {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(porter, $"/api/documents/{repoId}/children", new { name = "Legacy" })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(porter, $"/api/documents/{docId}/versions", new { fileExtension = ".txt", filedAt = "2003-01-02T00:00:00Z" });
        Assert.Contains($"/{tenantId}/2003/", version.GetProperty("uploadUrl").GetString()!);
    }

    [Fact]
    public async Task Create_refuses_to_grant_CanImport_the_caller_does_not_hold()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);

        const string password = "sair-1234";
        // Can manage service accounts, but does NOT hold CanImport — so it cannot hand CanImport out.
        var email = $"sa-noimport-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, password, "No Import", canManageServiceAccounts: true);
        using var caller = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var response = await caller.PostAsync("/api/service-accounts",
            JsonContent(new { name = $"nope-{Guid.NewGuid():N}", canImport = true }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INSUFFICIENT_RIGHTS_TO_GRANT", problem.RootElement.GetProperty("errorCode").GetString());
    }

    private static StringContent JsonContent(object value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
