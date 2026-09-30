using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// A module's rebuild must be REACHABLE, and must run the module's own rebuilder (#1507).
//
// WHY THIS EXISTS. `POST {moduleId}/rebuild/{projectionName}` describes itself as "the operator guarantee
// that a read model is never the only copy of anything, as a button-press: the support case's first answer" —
// and a module row advertised `license` and `settings` and nothing else, so by ADR 0543 no conforming client
// could find the button. It was reachable only by composing a path, which is what `saconsole module rebuild`
// is forbidden to do.
//
// A LISTING rather than a rel per projection, and that is the load-bearing choice: a rel whose name carried
// the projection (`rebuild:landings`) could not be followed by a client that does not already know the name,
// which is the composed-knowledge problem wearing a different costume. Nothing exposed *what can be rebuilt
// here* at all.
//
// WHAT THIS CANNOT PROVE, said plainly rather than implied: the collision #1507 is really about — a rebuild
// addressed to module A running module B's rebuilder, because the resolver matched on the projection NAME
// across every module in the container — needs two modules each declaring a projection, and the E2E harness
// loads one. The attribution is therefore asserted by construction (rebuilders are filtered to the
// assembly that declares the module) and by the negative case below, not by reproducing the collision.
[Collection(E2ECollection.Name)]
// e2e-1, the lighter leg — UiLegBalanceTests requires every class in a split suite to name its leg.
[Trait("Area", "e2e-1")]
public class ModuleProjectionsRelTests
{
    private readonly E2EApiFactory _factory;

    public ModuleProjectionsRelTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_active_module_advertises_its_projections_and_each_one_rebuilds()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Proj {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"projrel-{Guid.NewGuid():N}@e2e.local";
        const string password = "projrel-1234";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Projection Rel Admin",
            isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            // BEFORE activation: no projections rel, because the rebuild itself refuses for an inactive
            // module. A rel that is present and always refuses is the lying affordance ADR 0543 forbids.
            Assert.Null(ProjectionsHref(await ModuleRow(admin)));

            await ActivateAsync(owner, admin, tenantId, repoId, vendorKey);

            // AFTER: the rel appears, and is followed rather than composed.
            var projections = ProjectionsHref(await ModuleRow(admin));
            Assert.NotNull(projections);

            var listed = await TestJson.Get(admin, projections!);
            var rows = listed.GetProperty("projections").EnumerateArray().ToList();

            // The fixture module's own projection, and its rebuild address — which is what lets a client
            // rebuild without having been told the name "landings".
            var landings = Assert.Single(rows, r => r.GetProperty("name").GetString() == "landings");
            var rebuild = landings.GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "rebuild");

            Assert.Equal("POST", rebuild.GetProperty("method").GetString());

            var rebuilt = await admin.PostAsync(rebuild.GetProperty("href").GetString(), content: null);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, rebuilt.StatusCode);

            // THE NEGATIVE, which is the half that would have passed before the fix for the wrong reason: a
            // projection this module does not declare must 404 — not silently run somebody else's rebuilder
            // that happens to know the name.
            var foreign = await admin.PostAsync(
                $"/api/modules/test-module/rebuild/reader-certificates", content: null);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, foreign.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    private static string? ProjectionsHref(JsonElement row) =>
        row.TryGetProperty("links", out var links)
            ? links.EnumerateArray()
                .Where(l => l.GetProperty("rel").GetString() == "projections")
                .Select(l => l.GetProperty("href").GetString())
                .FirstOrDefault()
            : null;

    private static async Task<JsonElement> ModuleRow(HttpClient admin) =>
        (await TestJson.Get(admin, "/api/modules")).GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("moduleId").GetString() == "test-module");

    private async Task ActivateAsync(HttpClient owner, HttpClient admin, Guid tenantId, Guid repoId, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            ModuleAbiVersion.Major, string.Empty).Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
    }
}
