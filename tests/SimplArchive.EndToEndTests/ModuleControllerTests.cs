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
        // canManageServiceAccounts on top of tenant-admin: the consent act needs to FIND the module's
        // principal in the service-accounts listing, whose gate is that specific system right.
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Module Admin", isTenantAdmin: true, canManageServiceAccounts: true);
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

    private async Task ActivateAsync(Rig rig, ECDsa vendorKey, bool grantPrincipal = true)
    {
        var licenseDocId = await FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), vendorKey);
        await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = licenseDocId });
        if (grantPrincipal)
        {
            await GrantPrincipalAsync(rig);
        }
    }

    /// <summary>The consent act (ADR 0736): an ordinary ACL grant to the module's own login-less
    /// principal — created by the activation, listed like any service account.</summary>
    private static async Task GrantPrincipalAsync(Rig rig)
    {
        var principalId = (await TestJson.Get(rig.Admin, "/api/service-accounts"))
            .GetProperty("serviceAccounts").EnumerateArray()
            .Single(sa => sa.GetProperty("name").GetString() == "Module: Test Module")
            .GetProperty("id").GetGuid();
        await TestJson.Put(rig.Admin, $"/api/documents/{rig.RepoId}/acl-entries/service-accounts/{principalId}",
            new { canSee = true });
    }

    [Fact]
    public async Task A_transition_is_a_labeled_action_with_a_diagnosis_when_red_and_a_commit_when_green()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey, grantPrincipal: false);

            // A subject document — a dossier wearing the machine's subject mask (seeded at activation).
            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();

            // The generic action surface (ADR 0743): the transition arrives as a LABELED POST link — the
            // exact shape both clients' shipped parser turns into a button, rel unknown to either.
            var document = await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}");
            var action = document.GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "machine:test-pilot:log-entry");
            Assert.Equal("Log entry", action.GetProperty("label").GetString());
            Assert.Equal("POST", action.GetProperty("method").GetString());

            // CONSENT FIRST (ADR 0736): the module is active but UNGRANTED — its principal holds nothing,
            // so the machine's own reads see no evidence and the gate honestly refuses even before any
            // certificate question arises. The grant below is what changes the answer.
            await GrantPrincipalAsync(rig);

            // RED: no certificate filed — the refusal IS the diagnosis (ADR 0742), sentence in detail,
            // machine-readable codes in the problem's extensions.
            var refused = await rig.Admin.PostAsync(action.GetProperty("href").GetString(), null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
            Assert.Equal("MACHINE_TRANSITION_REFUSED", problem.GetProperty("errorCode").GetString());
            Assert.Contains(problem.GetProperty("refusals").EnumerateArray(),
                r => r.GetProperty("code").GetString() == "test.certificate-expired");

            // File a valid certificate, and the same click commits: the handler's write (a new child
            // through the facade) lands inside the engine's transaction.
            // An ITEM-masked child: created plain, then restamped — the children create-with-mask path
            // makes folders only, and a certificate is a document.
            var certificateId = (await TestJson.Post(rig.Owner, $"/api/documents/{dossierId}/children",
                new { name = "Medical" })).GetProperty("id").GetGuid();
            await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/mask",
                new { maskId = SimplArchive.TestModule.TestModule.CertificateMaskId });
            var validTo = (await TestJson.Get(rig.Admin, $"/api/masks/{SimplArchive.TestModule.TestModule.CertificateMaskId}"))
                .GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "Valid to").GetProperty("id").GetGuid();
            await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/index-data",
                new { fields = new[] { new { fieldDefinitionId = validTo, values = new[] { DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)).ToString("yyyy-MM-dd") } } } });

            var beforeGreen = await ChildCountAsync(rig.Admin, dossierId);
            Assert.Equal(HttpStatusCode.NoContent, (await rig.Admin.PostAsync(action.GetProperty("href").GetString(), null)).StatusCode);
            var afterGreen = await ChildCountAsync(rig.Admin, dossierId);
            Assert.Equal(beforeGreen + 1, afterGreen); // exactly the handler's one entry

            // ROLLBACK over the wire: the fixture's exploding handler writes and then throws — the caller
            // sees the failure, and what the handler wrote is never visible (ADR 0737's transaction).
            var exploded = await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/explode", null);
            Assert.Equal(HttpStatusCode.InternalServerError, exploded.StatusCode);
            Assert.Equal(afterGreen, await ChildCountAsync(rig.Admin, dossierId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public async Task The_fact_gated_act_reads_the_modules_projection_and_rebuild_rederives_it()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();
            await FileValidCertificateAsync(rig, dossierId);

            // The fact-gated act (ADRs 0736/0738 over the wire): refused while the module's OWN counter —
            // a real table the host migrated, not a computed aggregate — reads zero, with the value named.
            var refused = await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("0 recent landings",
                JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync())
                    .GetProperty("detail").GetString());

            // Three logged entries — each handler incrementing the projection in ITS act's transaction —
            // and the gate opens.
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HttpStatusCode.NoContent,
                    (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/log-entry", null)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.NoContent,
                (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null)).StatusCode);

            // Wipe the projection UNDER the module — the support-case scenario — and the gate honestly
            // closes...
            await using (var db = new Npgsql.NpgsqlConnection(Environment.GetEnvironmentVariable("ConnectionStrings__Default")))
            {
                await db.OpenAsync();
                await using var wipe = new Npgsql.NpgsqlCommand("DELETE FROM tm_landing_counters", db);
                await wipe.ExecuteNonQueryAsync();
            }

            Assert.Equal(HttpStatusCode.Conflict,
                (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null)).StatusCode);

            // ...until the REBUILD re-derives it from the documents (ADR 0738's operator guarantee): the
            // admin endpoint, then the same act passes again.
            Assert.Equal(HttpStatusCode.NoContent,
                (await rig.Admin.PostAsync("/api/modules/test-module/rebuild/landings", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,
                (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null)).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    private async Task FileValidCertificateAsync(Rig rig, Guid dossierId)
    {
        var certificateId = (await TestJson.Post(rig.Owner, $"/api/documents/{dossierId}/children",
            new { name = "Medical" })).GetProperty("id").GetGuid();
        await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/mask",
            new { maskId = SimplArchive.TestModule.TestModule.CertificateMaskId });
        var validTo = (await TestJson.Get(rig.Admin, $"/api/masks/{SimplArchive.TestModule.TestModule.CertificateMaskId}"))
            .GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Valid to").GetProperty("id").GetGuid();
        await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/index-data",
            new { fields = new[] { new { fieldDefinitionId = validTo, values = new[] { DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)).ToString("yyyy-MM-dd") } } } });
    }

    private static async Task<int> ChildCountAsync(HttpClient client, Guid folderId) =>
        (await TestJson.Get(client, $"/api/documents/{folderId}/children")).GetProperty("children").GetArrayLength();

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
