using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// Per-tenant module configuration (ADR 0772, ABI 0.12), over the real wire and the real loader: the module
// DECLARES its settings, the core renders and stores them, and the module reads them back through the facade.
//
// The security-relevant assertions are the boundary ones, and they are why this file exists rather than a
// controller unit test: a secret's value never crosses the wire to the administrator but DOES reach the
// module; an undeclared key is refused rather than stored; and the values are per tenant, so one tenant's
// credential is invisible to another even though both run the same module.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ModuleSettingsTests
{
    private readonly E2EApiFactory _factory;

    public ModuleSettingsTests(E2EApiFactory factory) => _factory = factory;

    private const string SettingsUrl = "/api/modules/test-module/settings";

    [Fact]
    public async Task The_form_is_what_the_module_declared_and_a_secret_never_comes_back()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);

            // The declarations reach the client even with nothing configured — that IS the form.
            var before = await TestJson.Get(rig.Admin, SettingsUrl);
            var declared = before.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["apiSecret", "endpoint"], declared.Select(i => i.GetProperty("key").GetString()).Order());
            Assert.All(declared, item => Assert.False(item.GetProperty("hasValue").GetBoolean()));

            await TestJson.Put(rig.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?>
                {
                    ["endpoint"] = "https://fixture.example",
                    ["apiSecret"] = "s3cr3t-value",
                },
            });

            var after = await TestJson.Get(rig.Admin, SettingsUrl);
            var endpoint = Item(after, "endpoint");
            var secret = Item(after, "apiSecret");

            // A plain setting reads back — the form could not show what it is about to change otherwise.
            Assert.True(endpoint.GetProperty("hasValue").GetBoolean());
            Assert.Equal("https://fixture.example", endpoint.GetProperty("value").GetString());

            // A secret reports only THAT it is set. This is the assertion the whole design turns on.
            Assert.True(secret.GetProperty("hasValue").GetBoolean());
            Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);
            Assert.DoesNotContain("s3cr3t-value", after.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    // The other half of write-only: what the admin surface withholds, the MODULE must still receive, or
    // storing a credential would be pointless. Read through the facade, inside the module's own endpoint.
    [Fact]
    public async Task The_module_reads_the_plaintext_the_administrator_cannot()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            await TestJson.Put(rig.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?>
                {
                    ["endpoint"] = "https://seen.example",
                    ["apiSecret"] = "only-the-module-sees-this",
                },
            });

            var seen = await TestJson.Get(rig.Admin, "/api/test-module/settings-seen");
            Assert.Equal("https://seen.example", seen.GetProperty("endpoint").GetString());
            Assert.Equal("only-the-module-sees-this", seen.GetProperty("apiSecret").GetString());

            // A key this module never declared reads as nothing — there is no cross-module reach by design.
            Assert.Equal(JsonValueKind.Null, seen.GetProperty("undeclared").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    // A merge, deliberately unlike the tenant-settings PUT: a client cannot read a secret back, so a full
    // replacement would blank every credential a form did not resend.
    [Fact]
    public async Task An_absent_key_is_left_alone_and_an_explicit_null_clears_it()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            await TestJson.Put(rig.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?> { ["endpoint"] = "https://kept.example", ["apiSecret"] = "kept-secret" },
            });

            // Change ONLY the endpoint — exactly what a form does when the secret box is left untouched.
            await TestJson.Put(rig.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?> { ["endpoint"] = "https://changed.example" },
            });

            var afterMerge = await TestJson.Get(rig.Admin, SettingsUrl);
            Assert.Equal("https://changed.example", Item(afterMerge, "endpoint").GetProperty("value").GetString());
            Assert.True(Item(afterMerge, "apiSecret").GetProperty("hasValue").GetBoolean());

            // The secret really survived — asked of the module, since the admin surface cannot say.
            Assert.Equal("kept-secret",
                (await TestJson.Get(rig.Admin, "/api/test-module/settings-seen")).GetProperty("apiSecret").GetString());

            // An explicit null is how a value is removed.
            await TestJson.Put(rig.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?> { ["apiSecret"] = null },
            });

            Assert.False(Item(await TestJson.Get(rig.Admin, SettingsUrl), "apiSecret").GetProperty("hasValue").GetBoolean());
            Assert.Equal(JsonValueKind.Null,
                (await TestJson.Get(rig.Admin, "/api/test-module/settings-seen")).GetProperty("apiSecret").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    // A key nothing declares is refused, not stored: a store that accepts anything is the free-form bag this
    // design avoids, and a typo would be written happily and never read back.
    [Fact]
    public async Task An_undeclared_key_is_refused()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);

            var response = await rig.Admin.PutAsJsonAsync(SettingsUrl, new
            {
                values = new Dictionary<string, string?> { ["endPoint"] = "a plausible typo" },
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("MODULE_SETTING_NOT_DECLARED", problem.GetProperty("errorCode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    // The point of the whole issue: one installation, many tenants, one credential each.
    [Fact]
    public async Task Two_tenants_running_the_same_module_hold_different_values()
    {
        var first = await RigAsync();
        var second = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(first, vendorKey);
            await ActivateAsync(second, vendorKey);

            await TestJson.Put(first.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?> { ["endpoint"] = "https://first.example" },
            });
            await TestJson.Put(second.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?> { ["endpoint"] = "https://second.example" },
            });

            Assert.Equal("https://first.example",
                Item(await TestJson.Get(first.Admin, SettingsUrl), "endpoint").GetProperty("value").GetString());
            Assert.Equal("https://second.example",
                Item(await TestJson.Get(second.Admin, SettingsUrl), "endpoint").GetProperty("value").GetString());

            // And the module itself sees its own tenant's value, not whichever was written last.
            Assert.Equal("https://first.example",
                (await TestJson.Get(first.Admin, "/api/test-module/settings-seen")).GetProperty("endpoint").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    // The rel is the affordance; the right is the enforcement. A plain member never sees the rel (they cannot
    // read the modules list at all), so this asserts the endpoint refuses them even holding the address.
    [Fact]
    public async Task A_non_admin_is_refused_even_holding_the_address()
    {
        var rig = await RigAsync();
        var email = $"modplain-{Guid.NewGuid():N}@e2e.local";
        const string password = "modsettings1234";
        await _factory.SeedUserAsync(rig.TenantId, email, password, "Plain Member");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync(SettingsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await plain.PutAsJsonAsync(SettingsUrl, new { values = new Dictionary<string, string?>() })).StatusCode);
    }

    // Standing convention: every GET action has a companion HEAD.
    [Fact]
    public async Task Head_mirrors_get()
    {
        var rig = await RigAsync();
        var head = await rig.Admin.SendAsync(new HttpRequestMessage(HttpMethod.Head, SettingsUrl));
        Assert.Equal(HttpStatusCode.NoContent, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        var missing = await rig.Admin.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/modules/no-such-module/settings"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static JsonElement Item(JsonElement settings, string key) =>
        settings.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    private sealed record Rig(HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId);

    private async Task<Rig> RigAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"ModSettings {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"modsettings-{Guid.NewGuid():N}@e2e.local";
        const string password = "modsettings1234";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Settings Admin",
            isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });

        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        return new Rig(admin, owner, tenantId, repoId);
    }

    private async Task ActivateAsync(Rig rig, ECDsa vendorKey)
    {
        var license = new ModuleLicense("test-module", rig.TenantId,
                DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), ModuleAbiVersion.Major, string.Empty)
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
        await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
    }
}
