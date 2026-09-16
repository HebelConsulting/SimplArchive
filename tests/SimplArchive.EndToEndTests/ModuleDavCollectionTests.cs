using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// A module-declared CalDAV collection must actually be SERVED, not merely declared (ABI 0.24, ADR 0791).
//
// WHY THIS EXISTS. The declaration side was covered — a module sets `DavCollection` on a folder mask and a unit
// test asserts the seed — but nothing ever asked whether the core then LISTS the collection and serves its
// items. That gap shipped: the flight-school Logbook declares itself correctly and is reported as neither
// visible in the Calendar tab nor subscribable over CalDAV (#1242). Every layer looked right in isolation,
// which is exactly the shape a gap between layers takes.
//
// It asks the question at the seam a client actually uses, twice over, because they can fail apart:
//   * `/api/dav-collections?kind=calendar` — what both clients' Calendar tabs read.
//   * the CalDAV home set — what a phone subscribes to.
//
// The TestModule gained its own `Test Log` / `Test Log Entry` masks for this rather than borrowing Dossier or
// Entry: additive fixture cannot change what other tests already assert.
[Collection(E2ECollection.Name)]
// e2e-1, the lighter leg (71 classes against e2e-2's 106) — UiLegBalanceTests requires every class in a split
// suite to name its leg, or the tier serialises behind whichever leg silently collected them.
[Trait("Area", "e2e-1")]
public class ModuleDavCollectionTests
{
    private readonly E2EApiFactory _factory;

    public ModuleDavCollectionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_module_declared_calendar_lists_and_serves_its_items()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Dav {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"davadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "davadmin-1234";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Dav Admin",
            isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(owner, admin, tenantId, repoId, vendorKey);

            // EXPERIMENT: nested one level, as a flight-school Logbook is (inside an Aircraft or a dossier)
            // rather than at the repository root.
            var dossierId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();
            var logId = (await TestJson.Post(owner, $"/api/documents/{dossierId}/children",
                new { name = $"Log {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.LogMaskId }))
                .GetProperty("id").GetGuid();

            // 1. THE LISTING both Calendar tabs read. A module's kind reaches it through the kind registry,
            //    keyed by extension — the list used to name its masks one by one, and that is precisely how
            //    Maintenance and Availability once stayed invisible while being served over CalDAV.
            var calendars = await TestJson.Get(admin, "/api/dav-collections?kind=calendar");
            var listed = calendars.GetProperty("collections").EnumerateArray()
                .Any(c => c.GetProperty("id").GetGuid() == logId);

            Assert.True(listed,
                "A module-declared .ics collection did not appear in /api/dav-collections?kind=calendar. "
                + "The module declares it (ModuleDavCollection on its folder mask) and the folder exists, so "
                + "the break is between the kind registry and the listing — which is what makes it invisible "
                + "in both clients' Calendar tabs (#1242, ADR 0791).");

            // 2. The CalDAV endpoint is reachable. THIS IS THE WEAK HALF AND SAYS SO: it asserts the protocol
            //    endpoint answers, NOT that the collection appears in a PROPFIND home set — CalDAV authenticates
            //    with the DAV password (Basic), which this fixture does not mint, so the subscribe path is not
            //    covered here. Listing and home set are separate code paths over the same registry and can fail
            //    apart, so do not read a green here as "subscribable" (#1242). Covering it properly needs a DAV
            //    credential in the fixture and belongs in its own test.
            var home = await admin.GetAsync("/caldav");
            Assert.True(home.IsSuccessStatusCode || home.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                $"The CalDAV root answered {(int)home.StatusCode}, which is neither a served home set nor the "
                + "Basic-auth challenge a DAV client expects — the protocol endpoint itself is broken.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    private async Task ActivateAsync(HttpClient owner, HttpClient admin, Guid tenantId, Guid repoId, ECDsa vendorKey)
    {
        var license = new ModuleLicense("test-module", tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
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

        // PUT on the version's own address, exactly as ModuleControllerTests.FileLicenseAsync does — the
        // confirm is a PUT to `/versions/{id}`, and the id is `id`, not `versionId`. Copied rather than
        // reconstructed: a replay built by analogy replays the author's assumptions, not the real call.
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
    }
}
