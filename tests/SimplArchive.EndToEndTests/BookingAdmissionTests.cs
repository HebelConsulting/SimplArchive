using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// The booking-admission seam (ABI 0.15, ADR 0781), over the real wire and the real loader: a module active
// for the tenant is shown every booking before it is saved, and may refuse it.
//
// Three outcomes, and the third is the one worth having tests for. A module that ADMITS changes nothing; a
// module that REFUSES answers with its own code and message; a module that is BROKEN refuses the booking
// rather than admitting it — the owner decision in ADR 0781, taken because admitting the unvetted fails
// silently and a consent rule that has quietly stopped applying is discovered by a double-booking.
//
// The handler is driven by an environment variable rather than a static, for the reason the verify key
// already is: the loader gives a module its own AssemblyLoadContext, so a static a test writes is not the
// static the module reads. One process environment crosses both.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class BookingAdmissionTests
{
    private const string ReviewSwitch = "SIMPLARCHIVE_TESTMODULE_BOOKING_REVIEW";

    private readonly E2EApiFactory _factory;

    public BookingAdmissionTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId, Guid RoomId);

    /// <summary>A tenant with the module ACTIVE and a bookable room in it. The room is a core Meeting room,
    /// deliberately: the seam is asked about every booking, not only about a module's own resources, and
    /// using a core resource proves that rather than assuming it.</summary>
    private async Task<Rig> RigAsync(ECDsa vendorKey)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Admission {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"admissionadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "modadmin-1234";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Admission Admin",
            isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var license = new ModuleLicense("test-module", tenantId,
            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), ModuleAbiVersion.Major, string.Empty).Sign(vendorKey);
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

        var masks = (await TestJson.Get(owner, "/api/masks")).GetProperty("masks").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetGuid());
        var roomId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = "Room 1", maskId = masks["Meeting room"] })).GetProperty("id").GetGuid();

        return new Rig(admin, owner, tenantId, repoId, roomId);
    }

    private static object Slot(int startHour, int endHour) => new
    {
        startsAt = new DateTimeOffset(2027, 5, 12, startHour, 0, 0, TimeSpan.Zero),
        endsAt = new DateTimeOffset(2027, 5, 12, endHour, 0, 0, TimeSpan.Zero),
        purpose = "Admission",
    };

    [Fact]
    public async Task A_module_that_admits_changes_nothing()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(ReviewSwitch, null);
        try
        {
            var rig = await RigAsync(vendorKey);

            var response = await rig.Owner.PostAsJsonAsync($"/api/documents/{rig.RoomId}/bookings", Slot(9, 10));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReviewSwitch, null);
        }
    }

    [Fact]
    public async Task A_module_refusal_answers_with_the_modules_own_code_and_the_booking_is_not_filed()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var rig = await RigAsync(vendorKey);
            Environment.SetEnvironmentVariable(ReviewSwitch, "refuse");

            var response = await rig.Owner.PostAsJsonAsync($"/api/documents/{rig.RoomId}/bookings", Slot(11, 12));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("TEST_BOOKING_REFUSED", problem.GetProperty("errorCode").GetString());

            // The module was shown the WHOLE booking, not a bare id: one claim, one of them holding, and a
            // first booking rather than a rebooking. Asserted through the refusal text because that is the
            // only channel a module's view of the request reaches the wire on.
            var detail = problem.GetProperty("detail").GetString()!;
            Assert.Contains("claims=1", detail, StringComparison.Ordinal);
            Assert.Contains("holding=1", detail, StringComparison.Ordinal);
            Assert.Contains("isNew=True", detail, StringComparison.Ordinal);

            // Refused BEFORE the save (ADR 0781), so nothing was left behind — the slot is still free.
            Environment.SetEnvironmentVariable(ReviewSwitch, null);
            var second = await rig.Owner.PostAsJsonAsync($"/api/documents/{rig.RoomId}/bookings", Slot(11, 12));
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);

            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReviewSwitch, null);
        }
    }

    [Fact]
    public async Task A_broken_handler_refuses_the_booking_rather_than_admitting_it()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var rig = await RigAsync(vendorKey);
            Environment.SetEnvironmentVariable(ReviewSwitch, "throw");

            var response = await rig.Owner.PostAsJsonAsync($"/api/documents/{rig.RoomId}/bookings", Slot(14, 15));

            // 503, not 409: nothing is wrong with this booking — it was never judged. A client that cannot
            // tell the two apart advises picking another time when the right advice is to call the school.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("BOOKING_VETTING_UNAVAILABLE", problem.GetProperty("errorCode").GetString());

            // The module is NAMED, so an administrator knows where to look rather than suspecting the schedule.
            Assert.Contains("test-module", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);

            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReviewSwitch, null);
        }
    }

    [Fact]
    public async Task A_tenant_without_the_module_is_not_vetted_by_it()
    {
        // The switch is ON, and a tenant with no activation books anyway: activation gates the seam, so a
        // module cannot refuse bookings in tenants that never licensed it. Without this, the three tests
        // above would pass just as well if the reviewer ignored activation entirely.
        Environment.SetEnvironmentVariable(ReviewSwitch, "refuse");
        try
        {
            var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
            using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
            var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Unvetted {Guid.NewGuid():N}" }))
                .GetProperty("id").GetGuid();
            var masks = (await TestJson.Get(api, "/api/masks")).GetProperty("masks").EnumerateArray()
                .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetGuid());
            var roomId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
                new { name = "Room 1", maskId = masks["Meeting room"] })).GetProperty("id").GetGuid();

            var response = await api.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(16, 17));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReviewSwitch, null);
        }
    }
}
