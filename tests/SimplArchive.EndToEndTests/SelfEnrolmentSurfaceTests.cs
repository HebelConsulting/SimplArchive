using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// What a person may enrol for themselves, as the modules declare it (ABI 1.3, ADR 0864, #1502).
//
// The assertion that justifies the whole shape is the FIRST one: an ordinary user — not a tenant
// administrator — can read this. The declaration's natural home looked like the modules listing, and that
// listing is admin-only because it carries licence state and settings rels, so a rel there would have been
// invisible to exactly the people this surface exists for. Owner-decided 2026-10-01.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class SelfEnrolmentSurfaceTests
{
    private const string SettingsUrl = "/api/modules/test-module/settings";

    private readonly E2EApiFactory _factory;

    public SelfEnrolmentSurfaceTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Owner, HttpClient Admin, HttpClient Member, Guid TenantId, Guid RepoId);

    [Fact]
    public async Task An_ORDINARY_user_can_read_what_they_may_enrol_and_the_gate_decides_the_address()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var rig = await RigAsync(vendorKey);

            // ─── Switched off (the setting is unset, which is NOT consent) ──────────────────────────────────
            var me = await TestJson.Get(rig.Member, "/api/me");
            var href = Links(me)["selfEnrolments"];

            var closed = await TestJson.Get(rig.Member, href);
            var row = Assert.Single(closed.GetProperty("items").EnumerateArray().ToList(),
                r => r.GetProperty("moduleId").GetString() == "test-module");

            Assert.False(row.GetProperty("enabled").GetBoolean());

            // PRESENT-AND-FALSE, not absent: "this installation does not do this" and "an administrator could
            // switch it on" are different facts, and a client can only say the second if it is told.
            Assert.DoesNotContain("enrol", Links(row).Keys);

            // ─── Switched on by an administrator ────────────────────────────────────────────────────────────
            await TestJson.Put(rig.Admin, SettingsUrl, new
            {
                values = new Dictionary<string, string?>
                {
                    ["selfEnrolmentOpen"] = "true",
                }
            });

            var open = await TestJson.Get(rig.Member, href);
            var opened = Assert.Single(open.GetProperty("items").EnumerateArray().ToList(),
                r => r.GetProperty("moduleId").GetString() == "test-module");

            Assert.True(opened.GetProperty("enabled").GetBoolean());

            // The address is the MODULE's statement of its own route — the core does not compose it.
            Assert.Equal("/api/modules/test-module/credential", Links(opened)["enrol"]);

            // And what the core needs in order to render a form on the module's behalf.
            Assert.Equal("Certificate", opened.GetProperty("input").GetString());
            Assert.Equal("certificatePem", opened.GetProperty("field").GetString());
            Assert.Equal("label", opened.GetProperty("labelField").GetString());
        }
        finally
        {
            // Process-global, on a fixture every E2E class shares — so it is withdrawn whatever happens.
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    [Fact]
    public async Task The_title_is_the_MODULES_own_wording_in_the_requests_language()
    {
        // The wording belongs to the module and this surface has four languages, so the title is a KEY
        // resolved through the module's catalog (ABI 0.10) rather than a literal. A literal would have
        // appeared in one language on a surface that has four — and a round trip against a literal proves
        // only that a string survives.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var rig = await RigAsync(vendorKey);
            var href = Links(await TestJson.Get(rig.Member, "/api/me"))["selfEnrolments"];

            Assert.Equal("Reader certificate", await TitleAsync(rig.Member, href, language: null));
            Assert.Equal("Lesezertifikat", await TitleAsync(rig.Member, href, language: "de"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    [Fact]
    public async Task A_tenant_whose_modules_declare_nothing_is_offered_nothing()
    {
        // The module is INSTALLED on this installation but not ACTIVE for this tenant, and an inactive
        // module's behaviour is absent rather than refused (ADR 0740) — so the row is absent, not present
        // and switched off. Those are different facts: one is an administrator's switch, the other is a
        // licence.
        var email = $"noenrol-{Guid.NewGuid():N}@e2e.local";
        const string password = "noenrol12345";
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        await _factory.SeedUserAsync(tenantId, email, password, "No Enrolment");
        using var member = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var href = Links(await TestJson.Get(member, "/api/me"))["selfEnrolments"];
        var listing = await TestJson.Get(member, href);

        Assert.DoesNotContain(listing.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("moduleId").GetString() == "test-module");
    }

    private static async Task<string> TitleAsync(HttpClient client, string href, string? language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, href);
        if (language is not null)
        {
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));
        }

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var listing = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return Assert.Single(listing.GetProperty("items").EnumerateArray().ToList(),
            r => r.GetProperty("moduleId").GetString() == "test-module")
            .GetProperty("title").GetString()!;
    }

    private static IReadOnlyDictionary<string, string> Links(JsonElement resource) =>
        resource.TryGetProperty("links", out var links)
            ? links.EnumerateArray().ToDictionary(
                l => l.GetProperty("rel").GetString()!, l => l.GetProperty("href").GetString()!)
            : new Dictionary<string, string>();

    /// <summary>A tenant with the test module activated, an administrator, and an ORDINARY member.</summary>
    private async Task<Rig> RigAsync(ECDsa vendorKey)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"SelfEnrol {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var adminEmail = $"selfenrol-admin-{Guid.NewGuid():N}@e2e.local";
        var memberEmail = $"selfenrol-member-{Guid.NewGuid():N}@e2e.local";
        const string password = "selfenrol1234";

        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Enrol Admin", isTenantAdmin: true);

        // Deliberately NOT an administrator: the point of this surface is that an ordinary person reaches it.
        await _factory.SeedUserAsync(tenantId, memberEmail, password, "Enrol Member");

        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));
        var member = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(memberEmail, password));

        var rig = new Rig(owner, admin, member, tenantId, repoId);
        await ActivateAsync(rig, vendorKey);
        return rig;
    }

    private async Task ActivateAsync(Rig rig, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], rig.TenantId,
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
