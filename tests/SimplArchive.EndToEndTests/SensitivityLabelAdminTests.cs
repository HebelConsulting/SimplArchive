using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for the configurable-label admin surface (ADR "Configurable sensitivity labels + upload defaults"):
// a CanManageClassification user creates/renames/retires labels (a non-manager is refused), and an upload
// auto-classified as a mask with a default label inherits that label.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SensitivityLabelAdminTests
{
    private readonly E2EApiFactory _factory;

    public SensitivityLabelAdminTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_rename_retire_and_authorize()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"clsadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "cls-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "ClsAdmin");
        await _factory.GrantTenantAdminAsync(email); // grants CanManageClassification
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // A non-manager (the plain service account owner) can't create a label.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/sensitivity-labels", new { name = "Nope", rank = 9, watermark = false })).StatusCode);

        // Create a custom watermarked label.
        var name = $"Secret-{Guid.NewGuid():N}"[..12];
        var id = (await (await admin.PostAsJsonAsync("/api/sensitivity-labels", new { name, rank = 9, color = "#000000", watermark = true })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var listed = (await TestJson.Get(admin, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == id);
        Assert.Equal(name, listed.GetProperty("name").GetString());
        Assert.True(listed.GetProperty("watermark").GetBoolean());
        Assert.True((await TestJson.Get(admin, "/api/sensitivity-labels")).GetProperty("canManage").GetBoolean());

        // A duplicate name → 409.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/sensitivity-labels", new { name, rank = 1, watermark = false })).StatusCode);

        // Rename it.
        var renamed = name + "-x";
        (await admin.PutAsJsonAsync($"/api/sensitivity-labels/{id}", new { name = renamed, rank = 9, color = "#000000", watermark = true })).EnsureSuccessStatusCode();
        Assert.Equal(renamed, (await TestJson.Get(admin, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == id).GetProperty("name").GetString());

        // Retire → still listed, flagged retired; un-retire clears it.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/sensitivity-labels/{id}")).StatusCode);
        Assert.True((await TestJson.Get(admin, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == id).GetProperty("retired").GetBoolean());
        (await admin.PostAsync($"/api/sensitivity-labels/{id}/unretire", null)).EnsureSuccessStatusCode();
        Assert.False((await TestJson.Get(admin, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == id).GetProperty("retired").GetBoolean());
    }

    [Fact]
    public async Task Upload_inherits_the_masks_default_label()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // Set the Basic Entry mask's upload default to "Internal" (a .txt auto-classifies as Basic Entry).
        var internalId = (await TestJson.Get(owner, "/api/sensitivity-labels")).GetProperty("labels").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Internal").GetProperty("id").GetGuid();
        await _factory.SetMaskDefaultSensitivityAsync(tenantId, "Basic Entry", internalId);

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Def {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "def-doc" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("body")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        // The finalized document, auto-classified as Basic Entry, inherited the mask's default label.
        var doc = await TestJson.Get(owner, $"/api/documents/{docId}");
        Assert.Equal(internalId, doc.GetProperty("sensitivityLabelId").GetGuid());
        Assert.Equal("Internal", doc.GetProperty("sensitivityLabelName").GetString());
    }
}
