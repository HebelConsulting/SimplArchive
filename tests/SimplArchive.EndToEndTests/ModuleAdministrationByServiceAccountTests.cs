using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// ADR 0870 over the real wire: a SERVICE ACCOUNT granted CanManageModules administers modules the way a tenant
// administrator does — finds the collection from the root, files and activates a licence, and is answered
// "yes" by the ABI 1.4 CanAdministerModulesAsync seam — while one without it is refused, the right cannot be
// handed out by somebody who does not hold it, and the licence it activates with must be one it can READ.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ModuleAdministrationByServiceAccountTests
{
    private readonly E2EApiFactory _factory;

    // One fixture password for every user this class seeds — a test credential, not a secret.
    private const string Password = "modsa-admin-1234";

    public ModuleAdministrationByServiceAccountTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId);

    private async Task<Rig> RigAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"ModAdm {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"modsa-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, Password, "Module SA Admin", isTenantAdmin: true, canManageServiceAccounts: true);
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, Password));
        return new Rig(admin, owner, tenantId, repoId);
    }

    // Created THROUGH the API by a tenant administrator — who holds the right by bypass, so may confer it.
    private async Task<(HttpClient Client, Guid Id)> CreateServiceAccountAsync(Rig rig, bool canManageModules)
    {
        var created = await TestJson.Post(rig.Admin, "/api/service-accounts",
            new { name = $"setup-{Guid.NewGuid():N}", canManageModules });
        Assert.Equal(canManageModules, created.GetProperty("canManageModules").GetBoolean());
        var token = await _factory.GetTokenAsync(
            created.GetProperty("clientId").GetString()!, created.GetProperty("clientSecret").GetString()!);
        return (_factory.CreateAuthedClient(token), created.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> FileLicenseAsync(HttpClient filer, Guid parentId, Guid tenantId, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            ModuleAbiVersion.Major, string.Empty).Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var docId = (await TestJson.Post(filer, $"/api/documents/{parentId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(filer, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(filer, $"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        return docId;
    }

    [Fact]
    public async Task A_service_account_with_the_right_reaches_and_activates_modules_and_one_without_is_refused()
    {
        var rig = await RigAsync();
        var (setup, setupId) = await CreateServiceAccountAsync(rig, canManageModules: true);
        var (plain, _) = await CreateServiceAccountAsync(rig, canManageModules: false);

        // The collection is reachable from the ROOT: tenant-settings, its other advertiser, refuses a machine.
        var root = await TestJson.Get(setup, "/api");
        var modulesHref = root.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "modules").GetProperty("href").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await setup.GetAsync(modulesHref)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync(modulesHref)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await setup.GetAsync("/api/tenant-settings")).StatusCode);

        // The licence goes where the setup account may read and file — and nowhere else.
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/service-accounts/{setupId}",
            new { canSee = true, canReadContent = true, canEditContent = true, canCreateSubItems = true });

        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var licenseId = await FileLicenseAsync(setup, rig.RepoId, rig.TenantId, vendorKey);

            var refused = await plain.PutAsync("/api/modules/test-module/license",
                JsonContent(new { licenseDocumentId = licenseId }));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            var activated = await TestJson.Put(setup, "/api/modules/test-module/license", new { licenseDocumentId = licenseId });
            Assert.True(activated.GetProperty("active").GetBoolean());

            // The ABI 1.4 seam answers the module's own question the same way the core did: a machine is never
            // a tenant administrator, yet it may administer modules.
            var status = await TestJson.Get(setup, "/api/test-module/status");
            Assert.False(status.GetProperty("isTenantAdmin").GetBoolean());
            Assert.True(status.GetProperty("canAdministerModules").GetBoolean());

            var plainStatus = await TestJson.Get(plain, "/api/test-module/status");
            Assert.False(plainStatus.GetProperty("canAdministerModules").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    [Fact]
    public async Task Activation_refuses_a_licence_document_the_caller_cannot_read_as_if_it_did_not_exist()
    {
        var rig = await RigAsync();
        var (setup, _) = await CreateServiceAccountAsync(rig, canManageModules: true);

        // Filed by the owner, in a repository the setup account holds NO grant on. Before ADR 0870 the activation
        // read any document by id, which was harmless only while the caller was always a tenant administrator.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licenseId = await FileLicenseAsync(rig.Owner, rig.RepoId, rig.TenantId, vendorKey);

        var response = await setup.PutAsync("/api/modules/test-module/license",
            JsonContent(new { licenseDocumentId = licenseId }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_a_holder_may_confer_the_right_on_create_and_on_edit()
    {
        var rig = await RigAsync();

        // Manages service accounts, but is NOT a tenant administrator — so does not hold CanManageModules.
        var email = $"modsa-mgr-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(rig.TenantId, email, Password, "SA Manager", canManageServiceAccounts: true);
        using var manager = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, Password));

        var listing = await TestJson.Get(manager, "/api/service-accounts");
        Assert.False(listing.GetProperty("grantableRights").GetProperty("canManageModules").GetBoolean());

        await AssertRefusedAsync(await manager.PostAsync("/api/service-accounts",
            JsonContent(new { name = $"esc-{Guid.NewGuid():N}", canManageModules = true })));

        var created = await TestJson.Post(manager, "/api/service-accounts", new { name = $"esc-{Guid.NewGuid():N}" });
        var address = $"/api/service-accounts/{created.GetProperty("id").GetGuid()}";
        await AssertRefusedAsync(await manager.PutAsync(address,
            JsonContent(new { name = "still-esc", canManageModules = true })));

        // The same hole existed for CanBlockResources, capped on create only until this change.
        await AssertRefusedAsync(await manager.PutAsync(address,
            JsonContent(new { name = "still-esc", canBlockResources = true })));
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INSUFFICIENT_RIGHTS_TO_GRANT", problem.RootElement.GetProperty("errorCode").GetString());
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
