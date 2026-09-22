using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task The_populate_hook_fills_a_module_calendar_on_the_calendars_own_poll_rate_limited()
    {
        // ABI 0.28 (#1307, ADR 0815): ADR 0810's deferred surface, wired. Three claims, in one flow, because
        // they can only be proven together: (1) a CalDAV enumeration RUNS the hook — here PROPFIND, the
        // setup-time door; (2) the declared minimum refresh interval is the brake for DURABLE items (the
        // Test Log's entries carry no ExpiresAt, so ADR 0810's cooldown can never engage — every further
        // poll must be a no-op); (3) the populate runs BEFORE the sync-token reconcile, so a freshly filed
        // item appears in the very sync-collection response that triggered it — populating after the token
        // tells the client "nothing new" about an item it has never seen, the audit's silent wrong answer.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Dav {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"davpop-{Guid.NewGuid():N}@e2e.local";
        const string password = "davpop-1234";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Dav Populate Admin",
            isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(owner, admin, tenantId, repoId, vendorKey);

            var logId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
                new { name = $"Log {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.LogMaskId }))
                .GetProperty("id").GetGuid();

            // CONSENT for the module principal (ADR 0736): the hook's handler counts the collection's
            // existing entries through the facade, and a read the principal is not granted returns EMPTY
            // rather than failing — so without this the second run re-derives entry number 1, collides on
            // the sibling name, and the populate degrades silently into "nothing new" forever.
            var principalId = (await TestJson.Get(admin, "/api/service-accounts"))
                .GetProperty("serviceAccounts").EnumerateArray()
                .Single(sa => sa.GetProperty("name").GetString() == "Module: Test Module")
                .GetProperty("id").GetGuid();
            await TestJson.Put(admin, $"/api/documents/{repoId}/acl-entries/service-accounts/{principalId}",
                new { canSee = true });

            // Both halves of ADR 0810's double gate: the module declared eligibility; the tenant enables.
            await TestJson.Put(admin, "/api/modules/test-module/settings", new
            {
                values = new Dictionary<string, string?> { [ProtocolReadRefreshSetting.Key] = "true" },
            });

            var davPassword = (await TestJson.Post(admin, "/api/me/webdav-password", new { }))
                .GetProperty("password").GetString()!;
            var auth = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
            using var dav = _factory.CreateClient();
            var collectionHref = $"/caldav/calendars/{logId}/";

            // (1) The first enumeration runs the hook and serves what it filed — in the SAME response.
            var propfind = await DavAsync(dav, auth, "PROPFIND", collectionHref,
                """<?xml version="1.0"?><D:propfind xmlns:D="DAV:"><D:prop><D:getetag/></D:prop></D:propfind>""");
            Assert.Equal(207, (int)propfind.StatusCode);
            Assert.Contains("test-populated-1", await propfind.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(1, await ChildrenCountAsync(admin, logId));

            // (2) The interval is the ONLY brake here, and it must hold: another door, same clock.
            var initialSync = await DavAsync(dav, auth, "REPORT", collectionHref,
                """<?xml version="1.0"?><D:sync-collection xmlns:D="DAV:"><D:sync-token/><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>""");
            var initialBody = await initialSync.Content.ReadAsStringAsync();
            Assert.Equal(207, (int)initialSync.StatusCode);
            Assert.Contains("test-populated-1", initialBody, StringComparison.Ordinal);
            Assert.Equal(1, await ChildrenCountAsync(admin, logId));
            var token = System.Xml.Linq.XDocument.Parse(initialBody).Descendants()
                .Single(e => e.Name.LocalName == "sync-token").Value;

            // (3) Age the attempt clock past the declared interval — the way time would — straight in the
            // database: the row is machine-owned bookkeeping and this is the machine's hand.
            using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(_factory.Services))
            {
                var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>(scope.ServiceProvider);
                var attempt = await db.ModulePopulateAttempts.IgnoreQueryFilters()
                    .SingleAsync(a => a.SubjectDocumentId == logId);
                attempt.LastAttemptAt = DateTimeOffset.UtcNow.AddHours(-2);
                await db.SaveChangesAsync();
            }

            // The incremental sync now: the populate runs INSIDE this request, before the reconcile — so
            // the brand-new second entry is in this very response, not silently absent until the next poll.
            var incremental = await DavAsync(dav, auth, "REPORT", collectionHref,
                $"""<?xml version="1.0"?><D:sync-collection xmlns:D="DAV:"><D:sync-token>{token}</D:sync-token><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>""");
            var incrementalBody = await incremental.Content.ReadAsStringAsync();
            Assert.Equal(207, (int)incremental.StatusCode);
            Assert.Contains("test-populated-2", incrementalBody, StringComparison.Ordinal);
            Assert.Equal(2, await ChildrenCountAsync(admin, logId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    private static async Task<HttpResponseMessage> DavAsync(
        HttpClient client, System.Net.Http.Headers.AuthenticationHeaderValue auth, string method, string url, string body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Headers = { Authorization = auth },
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        request.Headers.TryAddWithoutValidation("Depth", "1");
        return await client.SendAsync(request);
    }

    private static async Task<int> ChildrenCountAsync(HttpClient api, Guid folderId) =>
        (await TestJson.Get(api, $"/api/documents/{folderId}/children")).GetProperty("children").GetArrayLength();

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
