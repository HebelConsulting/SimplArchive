using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;
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

    private sealed record Rig(
        HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId, Guid RoomId,
        Guid AdminUserId, string AdminEmail, string AdminPassword);

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

        return new Rig(admin, owner, tenantId, repoId, roomId, adminId, email, password);
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

            // The claim the booking is being MADE for must report itself as new. This endpoint creates and
            // SAVES the claim row before the finalizer runs, so the change tracker calls it Unchanged by
            // review time — and a consent rule keyed on "is this claimant new?" would skip the only claimant
            // there is, admitting every booking ever made through the app. It did exactly that until the
            // real demo stack was driven; every test here was green throughout.
            Assert.Contains("newClaims=1", detail, StringComparison.Ordinal);

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
    public async Task The_review_reads_as_the_module_not_as_whoever_is_writing()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var rig = await RigAsync(vendorKey);

            // A document in a SEPARATE repository that the module's principal may see and the booking's
            // writer may not. That asymmetry is the whole test: it is the only way to tell which identity
            // the reads behind a vetting rule are running under.
            //
            // Created by the OWNER (the service account holding canManageRepositories), and the writer is a
            // PLAIN user granted only on the room's repository — neither of the rig's other principals can
            // play that part, since a tenant admin sees everything by bypass and would make seen=True
            // meaningless.
            var vaultId = (await TestJson.Post(rig.Owner, "/api/repositories", new { name = $"Vault {Guid.NewGuid():N}" }))
                .GetProperty("id").GetGuid();
            var secretId = (await TestJson.Post(rig.Owner, $"/api/documents/{vaultId}/children",
                new { name = $"Module only {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

            var pilotEmail = $"pilot-{Guid.NewGuid():N}@e2e.local";
            const string pilotPassword = "modadmin-1234";
            var pilotId = await _factory.SeedUserAsync(rig.TenantId, pilotEmail, pilotPassword, "Pilot");
            await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{pilotId}",
                new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
            using var writer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(pilotEmail, pilotPassword));

            var principalId = (await TestJson.Get(rig.Admin, "/api/service-accounts"))
                .GetProperty("serviceAccounts").EnumerateArray()
                .Single(sa => sa.GetProperty("name").GetString() == "Module: Test Module")
                .GetProperty("id").GetGuid();
            await TestJson.Put(rig.Admin, $"/api/documents/{vaultId}/acl-entries/service-accounts/{principalId}",
                new { canSee = true, canReadContent = true });

            // The writer was never granted anything on that repository, and confirms it: without this the
            // test would pass just as well if BOTH identities could read the document.
            var denied = await writer.GetAsync($"/api/documents/{secretId}");
            Assert.False(denied.IsSuccessStatusCode,
                $"The writer could read {secretId}, so seen=True below would prove nothing. "
                + "This guard is what stops the test passing by accident.");

            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_PROBE_DOCUMENT", secretId.ToString());
            Environment.SetEnvironmentVariable(ReviewSwitch, "probe");

            var response = await writer.PostAsJsonAsync($"/api/documents/{rig.RoomId}/bookings", Slot(18, 19));

            var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("TEST_BOOKING_PROBE", problem.GetProperty("errorCode").GetString());

            // seen=True: the module read what only the MODULE may read, while the writer could not. Were the
            // review running as the writer this would be seen=False — and the consent rule's verdict would
            // then depend on who wrote the booking rather than on the booking.
            Assert.Contains("seen=True", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);

            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReviewSwitch, null);
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_PROBE_DOCUMENT", null);
        }
    }

    /// <summary>The href of a room's Schedule collection, from the CalDAV home set.</summary>
    private static async Task<string> ScheduleHrefAsync(HttpClient dav, AuthenticationHeaderValue basic, string roomName)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/caldav/calendars/")
        {
            Headers = { Authorization = basic },
            Content = new StringContent(
                "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>", Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("Depth", "1");
        using var response = await dav.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);

        // Split case-insensitively: this server emits lowercase element names, so splitting on the
        // upper-case spelling silently yields ONE block containing everything.
        var blocks = System.Text.RegularExpressions.Regex.Split(
            body, "</[a-zA-Z]+:response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var block = blocks.FirstOrDefault(b => b.Contains($"{roomName} / Schedule", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No '{roomName} / Schedule' in the home set. Body: {body}");
        return System.Text.RegularExpressions.Regex.Match(block, "<[^>]*href[^>]*>([^<]+)</").Groups[1].Value;
    }

    [Fact]
    public async Task Every_claim_is_shown_including_the_ones_an_attendee_added()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var rig = await RigAsync(vendorKey);

            // A Schedule to PUT into: assigning the Meeting-room mask does not create one, and this rig has
            // not booked through the endpoint that would (noted in ADR 0776).
            var scheduleId = (await TestJson.Post(rig.Admin, $"/api/documents/{rig.RoomId}/children",
                new { name = "Schedule" })).GetProperty("id").GetGuid();
            await TestJson.Put(rig.Admin, $"/api/documents/{scheduleId}/mask",
                new { maskId = SimplArchive.Domain.Masks.WellKnownMaskIds.Schedule });

            // A second bookable document standing for the writer, so an ATTENDEE naming them becomes a claim.
            var personId = (await TestJson.Post(rig.Admin, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Person {Guid.NewGuid():N}"[..16] })).GetProperty("id").GetGuid();
            await TestJson.Put(rig.Admin, $"/api/documents/{personId}/mask",
                new { maskId = SimplArchive.Domain.Masks.WellKnownMaskIds.MeetingRoom });
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
                db.ResourcePrincipals.Add(new SimplArchive.Domain.Booking.ResourcePrincipal
                {
                    Id = Guid.NewGuid(),
                    TenantId = rig.TenantId,
                    ResourceDocumentId = personId,
                    UserId = rig.AdminUserId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var davPassword = (await TestJson.Post(rig.Admin, "/api/me/webdav-password", new { }))
                .GetProperty("password").GetString()!;
            var basic = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rig.AdminEmail}:{davPassword}")));

            Environment.SetEnvironmentVariable(ReviewSwitch, "refuse");

            var uid = Guid.NewGuid().ToString();
            var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n"
                + $"UID:{uid}\r\nSUMMARY:Attendee booking\r\n"
                + "DTSTART:20270512T200000Z\r\nDTEND:20270512T210000Z\r\n"
                + $"ATTENDEE:mailto:{rig.AdminEmail}\r\n"
                + "END:VEVENT\r\nEND:VCALENDAR\r\n";

            using var dav = _factory.CreateClient();

            // The collection's href is DISCOVERED, never composed: the server owns its URL space, and a
            // guessed path answers with an empty body that reads exactly like "the module saw no claims".
            var schedule = await ScheduleHrefAsync(dav, basic, "Room 1");

            using var request = new HttpRequestMessage(HttpMethod.Put, $"{schedule}{uid}.ics")
            {
                Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
            };
            request.Headers.Authorization = basic;
            var response = await dav.SendAsync(request);

            var body = await response.Content.ReadAsStringAsync();

            // TWO claims: the room that holds the .ics, and the person the ATTENDEE resolved to.
            //
            // This is the assertion that matters. The attendee's claim is added during reconciliation, and it
            // was being added to the DbContext WITHOUT being added to the claim list the review is built
            // from — so a module vetting a flight saw the aircraft and neither of the people on it, which is
            // the one thing a consent rule exists to look at. The single-claim tests above passed throughout.
            Assert.Contains("claims=2", body, StringComparison.Ordinal);
            Assert.Contains("holding=1", body, StringComparison.Ordinal);
            Assert.Contains("newClaims=2", body, StringComparison.Ordinal);
            Assert.Contains("slotChanged=False", body, StringComparison.Ordinal);
            Assert.Contains("dropped=0", body, StringComparison.Ordinal);

            // Now take an attendee OFF a flight. A SECOND booking, deliberately: the write above was
            // REFUSED, and re-using its UID would be editing whatever husk that left rather than creating
            // one cleanly — which is exactly how this assertion first failed.
            Environment.SetEnvironmentVariable(ReviewSwitch, null);
            var liveUid = Guid.NewGuid().ToString();
            var liveIcs = ics.Replace(uid, liveUid, StringComparison.Ordinal)
                .Replace("20270512T200000Z", "20270512T220000Z", StringComparison.Ordinal)
                .Replace("20270512T210000Z", "20270512T230000Z", StringComparison.Ordinal);

            using (var create = new HttpRequestMessage(HttpMethod.Put, $"{schedule}{liveUid}.ics")
            {
                Content = new StringContent(liveIcs, Encoding.UTF8, "text/calendar"),
            })
            {
                create.Headers.Authorization = basic;
                (await dav.SendAsync(create)).EnsureSuccessStatusCode();
            }

            Environment.SetEnvironmentVariable(ReviewSwitch, "refuse");
            var without = liveIcs.Replace($"ATTENDEE:mailto:{rig.AdminEmail}\r\n", string.Empty, StringComparison.Ordinal);
            using var drop = new HttpRequestMessage(HttpMethod.Put, $"{schedule}{liveUid}.ics")
            {
                Content = new StringContent(without, Encoding.UTF8, "text/calendar"),
            };
            drop.Headers.Authorization = basic;
            var dropBody = await (await dav.SendAsync(drop)).Content.ReadAsStringAsync();

            // The module is shown that somebody is LEAVING — without it a rule about withdrawal cannot
            // exist — and the departing claim is still LISTED, because judging a removal means seeing who
            // is going (ADR 0784).
            Assert.Contains("dropped=1", dropBody, StringComparison.Ordinal);
            Assert.Contains("claims=2", dropBody, StringComparison.Ordinal);

            // A REFUSED write leaves no husk. DavWrites purges the document a refused booking created, but
            // caught only the core's BookingException — so a MODULE's refusal (a ModuleApiException) slipped
            // past it and left a phantom entry in the resource's Schedule with no claim behind it, which a
            // retrying calendar client then met as an unrelated sibling-name 409.
            //
            // Asserted on the COLLECTION rather than on the response, because the husk is invisible in the
            // answer the client gets — it only shows up as an entry nobody made.
            var ghostUid = Guid.NewGuid().ToString();
            var ghostIcs = ics.Replace(uid, ghostUid, StringComparison.Ordinal)
                .Replace("20270512T200000Z", "20270513T080000Z", StringComparison.Ordinal)
                .Replace("20270512T210000Z", "20270513T090000Z", StringComparison.Ordinal);
            using (var ghost = new HttpRequestMessage(HttpMethod.Put, $"{schedule}{ghostUid}.ics")
            {
                Content = new StringContent(ghostIcs, Encoding.UTF8, "text/calendar"),
            })
            {
                ghost.Headers.Authorization = basic;
                var ghostResponse = await dav.SendAsync(ghost);
                Assert.Equal(HttpStatusCode.Conflict, ghostResponse.StatusCode);

                // And the refusal still EXPLAINS itself: purging the husk must not swallow the module's
                // message, which was the first version of that fix.
                Assert.Contains("TEST_BOOKING_REFUSED",
                    await ghostResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            var listing = await TestJson.Get(rig.Admin, $"/api/documents/{scheduleId}/children");
            var remaining = listing.GetProperty(listing.TryGetProperty("items", out _) ? "items" : "children")
                .EnumerateArray()
                .Select(i => i.GetProperty("name").GetString()!)
                .ToList();
            Assert.DoesNotContain(remaining, n => n.Contains(ghostUid, StringComparison.Ordinal));

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
