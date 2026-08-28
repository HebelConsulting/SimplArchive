using System.Net;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API for ADR 0534: an existing service account's name + rights are editable via
// PUT /api/service-accounts/{id}, under the same escalation cap as create — a caller can only set a right to
// true that it holds itself. Backs the desktop + web service-account management UIs.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ServiceAccountEditTests
{
    private readonly E2EApiFactory _factory;

    public ServiceAccountEditTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Update_edits_name_and_rights_and_the_change_persists()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);

        const string password = "saedit-1234";
        // The admin caller holds CanExport, so it may grant CanExport when editing.
        var adminEmail = $"sa-editadmin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "SA Edit Admin", canExport: true, canManageServiceAccounts: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        // Create a bare account (no rights), then edit it to add CanExport and rename it.
        var created = await TestJson.Post(admin, "/api/service-accounts", new { name = $"editme-{Guid.NewGuid():N}" });
        var id = created.GetProperty("id").GetGuid();
        Assert.False(created.GetProperty("canExport").GetBoolean());

        var newName = $"edited-{Guid.NewGuid():N}";
        var updated = await TestJson.Put(admin, $"/api/service-accounts/{id}", new { name = newName, canExport = true });
        Assert.Equal(newName, updated.GetProperty("name").GetString());
        Assert.True(updated.GetProperty("canExport").GetBoolean());

        // GET confirms the edit persisted.
        var fetched = await TestJson.Get(admin, $"/api/service-accounts/{id}");
        Assert.Equal(newName, fetched.GetProperty("name").GetString());
        Assert.True(fetched.GetProperty("canExport").GetBoolean());
    }

    [Fact]
    public async Task Update_refuses_to_grant_a_right_the_caller_does_not_hold()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);

        const string password = "saedit-1234";
        // Can manage service accounts, but does NOT hold CanExport — so it cannot grant CanExport on edit.
        var email = $"sa-noexport-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, password, "No Export", canManageServiceAccounts: true);
        using var caller = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var created = await TestJson.Post(caller, "/api/service-accounts", new { name = $"editme-{Guid.NewGuid():N}" });
        var id = created.GetProperty("id").GetGuid();

        var response = await caller.PutAsync($"/api/service-accounts/{id}",
            JsonContent(new { name = "still-editme", canExport = true }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INSUFFICIENT_RIGHTS_TO_GRANT", problem.RootElement.GetProperty("errorCode").GetString());
    }

    private static StringContent JsonContent(object value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
