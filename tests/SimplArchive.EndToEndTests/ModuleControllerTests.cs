using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// The module-controller slice's activation circle (ADR 0737), over the real wire and the REAL loader (the
// TestModule dll is staged into a Modules/ directory by the factory): before activation the module's
// surface does not exist — no root rel, 404 MODULE_NOT_ACTIVE on its routes; filing a vendor-signed
// license activates it — the rel appears, the controller answers through the ABI's caller/rights seams,
// its refusals wear the core's RFC 7807 shape; and a license lapsed past grace switches it all off again.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ModuleControllerTests
{
    private readonly E2EApiFactory _factory;

    public ModuleControllerTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId);

    private async Task<Rig> RigAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Mods {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"modadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "modadmin-1234";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Module Admin", isTenantAdmin: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        return new Rig(admin, owner, tenantId, repoId);
    }

    private static async Task<bool> RootAdvertisesAsync(HttpClient client) =>
        (await TestJson.Get(client, "/api")).GetProperty("links").EnumerateArray()
            .Any(l => l.GetProperty("rel").GetString() == "test-module:status");

    private static async Task<Guid> FileLicenseAsync(Rig rig, DateOnly supportEnd, ECDsa vendorKey)
    {
        var license = new ModuleLicense("test-module", rig.TenantId, supportEnd, ModuleAbiVersion.Major, string.Empty)
            .Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var docId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(rig.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(rig.Owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        return docId;
    }

    [Fact]
    public async Task The_activation_circle_switches_the_modules_surface_on_and_off()
    {
        var rig = await RigAsync();

        // BEFORE: the module's surface does not exist for this tenant — no rel at the root (ADR 0543),
        // and the route answers 404 with the reason named for an administrator reading the wire.
        Assert.False(await RootAdvertisesAsync(rig.Admin));
        var refused = await rig.Admin.GetAsync("/api/test-module/status");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
        Assert.Equal("MODULE_NOT_ACTIVE", problem.GetProperty("errorCode").GetString());

        // ACTIVATE: plant the vendor verify key (environment, because the loaded module lives in its own
        // context — no key material in the repo), file a signed license, and follow the license PUT.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var licenseDocId = await FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), vendorKey);
            await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = licenseDocId });

            // AFTER: the rel exists, the controller answers through the seams, and a module refusal is an
            // RFC 7807 problem indistinguishable in shape from a core one.
            Assert.True(await RootAdvertisesAsync(rig.Admin));
            var status = await TestJson.Get(rig.Admin, "/api/test-module/status");
            Assert.Equal(rig.TenantId, status.GetProperty("tenantId").GetGuid());
            Assert.True(status.GetProperty("isTenantAdmin").GetBoolean());
            Assert.Contains(status.GetProperty("links").EnumerateArray(),
                l => l.GetProperty("rel").GetString() == "self");

            // The rights seam: the caller's effective rights on a real document, answered by the core
            // calculator — visible document → the module sees CanSee...
            var rights = await TestJson.Get(rig.Admin, $"/api/test-module/documents/{licenseDocId}/rights");
            Assert.True(rights.GetProperty("canSee").GetBoolean());

            // ...unknown document → the module's own intent-named refusal, in the core's problem shape.
            var invisible = await rig.Admin.GetAsync($"/api/test-module/documents/{Guid.NewGuid()}/rights");
            Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode);
            Assert.Equal("TEST_DOCUMENT_NOT_VISIBLE",
                JsonSerializer.Deserialize<JsonElement>(await invisible.Content.ReadAsStringAsync())
                    .GetProperty("errorCode").GetString());

            // LAPSE: a genuine renewal whose support contract ended beyond the 30-day grace — the derived
            // active answer flips (ADR 0740) and the whole surface withdraws again.
            var lapsedDocId = await FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-40)), vendorKey);
            await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = lapsedDocId });

            Assert.False(await RootAdvertisesAsync(rig.Admin));
            Assert.Equal(HttpStatusCode.NotFound, (await rig.Admin.GetAsync("/api/test-module/status")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }
}
