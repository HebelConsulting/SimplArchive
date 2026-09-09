using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// The maintenance block, end to end over the surface people actually use (ADR 0778): an .ics written into a
// resource's Maintenance collection.
//
// Driven through CalDAV rather than a controller because there IS no block controller — the block IS the
// .ics, the same way the booking is (ADR 0744), so the write path is the classifier's single door and that is
// what has to be right. The three facts pinned here are the ones the design turns on: the right is enforced,
// clearing is a WRITE (so the release can be gated and recorded at that same door), and a booking made into a
// block is refused with its own code rather than as a slot conflict.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class MaintenanceBlockTests
{
    private readonly E2EApiFactory _factory;

    public MaintenanceBlockTests(E2EApiFactory factory) => _factory = factory;

    private sealed record World(HttpClient Api, HttpClient Dav, AuthenticationHeaderValue Basic, Guid RoomId, string RoomName, string MaintenanceHref);

    // A bookable room with its Maintenance collection, plus a DAV credential for the seeded user.
    private async Task<World> SeedAsync(bool canBlock, bool canRelease)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"mb-{Guid.NewGuid():N}@e2e.local";
        const string password = "mb-1234";

        // Deliberately NOT a tenant admin: an admin holds both rights implicitly, which would make every
        // refusal in this file unreachable and the tests vacuous.
        await _factory.SeedUserAsync(tenantId, email, password, "Blocker",
            canManageRepositories: true, canViewAuditLog: true,
            canBlockResources: canBlock, canReleaseResources: canRelease);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repositoryId = (await TestJson.Post(api, "/api/repositories", new { name = $"mb-{Guid.NewGuid():N}"[..18] }))
            .GetProperty("id").GetGuid();
        var roomName = $"Room {Guid.NewGuid():N}"[..14];
        var roomId = (await TestJson.Post(api, $"/api/documents/{repositoryId}/children", new { name = roomName }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{roomId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });

        // Both collections, explicitly: the booking flow creates the Schedule lazily (#1097) and nothing
        // creates the Maintenance collection at all, so a test that assumed either would be testing the
        // fixture rather than the feature.
        var scheduleId = (await TestJson.Post(api, $"/api/documents/{roomId}/children", new { name = "Schedule" }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{scheduleId}/mask", new { maskId = WellKnownMaskIds.Schedule });
        var maintenanceId = (await TestJson.Post(api, $"/api/documents/{roomId}/children", new { name = "Maintenance" }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{maintenanceId}/mask", new { maskId = WellKnownMaskIds.Maintenance });

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        var dav = _factory.CreateClient();

        return new World(api, dav, basic, roomId, roomName, await MaintenanceHrefAsync(dav, basic, roomName));
    }

    // The collection's DAV address, found the way a client finds it: from the home set.
    private static async Task<string> MaintenanceHrefAsync(HttpClient dav, AuthenticationHeaderValue basic, string roomName)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/caldav/calendars/") { Headers = { Authorization = basic } };
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>", Encoding.UTF8, "text/xml");
        using var home = await dav.SendAsync(request);
        var body = await home.Content.ReadAsStringAsync();

        // Case-insensitively, because the server emits lowercase element names: splitting on the exact
        // "</D:response>" made the whole document one block, so every assertion silently ran against the home
        // set instead of the collection.
        var block = System.Text.RegularExpressions.Regex
            .Split(body, "</[a-zA-Z]+:response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .First(b => b.Contains($"{roomName} / Maintenance", StringComparison.Ordinal));
        return System.Text.RegularExpressions.Regex.Match(block, "<[^>]*href[^>]*>([^<]+)</").Groups[1].Value;
    }

    private static string BlockIcs(string uid, string status = "CONFIRMED") =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//e2e//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20270601T090000Z\r\nDTSTART:20270604T080000Z\r\nDTEND:20270604T180000Z\r\n"
        + $"STATUS:{status}\r\nSUMMARY:50h check\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static async Task<HttpResponseMessage> PutBlockAsync(World w, string uid, string status = "CONFIRMED")
    {
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{w.MaintenanceHref}{uid}.ics") { Headers = { Authorization = w.Basic } };
        put.Content = new StringContent(BlockIcs(uid, status), Encoding.UTF8, "text/calendar");
        return await w.Dav.SendAsync(put);
    }

    [Fact]
    public async Task Grounding_a_resource_without_the_right_is_refused()
    {
        var w = await SeedAsync(canBlock: false, canRelease: false);
        using var _a = w.Api;
        using var _d = w.Dav;

        using var response = await PutBlockAsync(w, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Grounding_a_resource_with_the_right_blocks_the_window()
    {
        var w = await SeedAsync(canBlock: true, canRelease: false);
        using var _a = w.Api;
        using var _d = w.Dav;

        (await PutBlockAsync(w, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();

        // ...and a booking inside it is refused with the BLOCK's code, not the slot conflict's — the two are
        // different facts with different remedies, and a caller that cannot tell them apart sends the pilot
        // hunting for a free hour that does not exist.
        var start = new DateTimeOffset(2027, 6, 4, 10, 0, 0, TimeSpan.Zero);
        using var booking = await w.Api.PostAsJsonAsync($"/api/documents/{w.RoomId}/bookings",
            new { startsAt = start, endsAt = start.AddHours(1), purpose = "Should be refused" });

        Assert.Equal(HttpStatusCode.Conflict, booking.StatusCode);
        Assert.Contains("RESOURCE_BLOCKED", await booking.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Clearing is a WRITE, not a delete (ADR 0778) — which is the whole reason the release can be gated at
    // all. Deleting the document clears the row too, but that happens inside SaveChanges where no right can
    // be checked and no audit event recorded.
    [Fact]
    public async Task Releasing_without_the_release_right_is_refused_even_by_the_person_who_grounded_it()
    {
        var w = await SeedAsync(canBlock: true, canRelease: false);
        using var _a = w.Api;
        using var _d = w.Dav;

        var uid = Guid.NewGuid().ToString();
        (await PutBlockAsync(w, uid)).EnsureSuccessStatusCode();

        using var release = await PutBlockAsync(w, uid, "CANCELLED");

        // The decided rule, and the one most likely to be "tidied" later into "you may undo your own block":
        // a release to service is the same act regardless of who grounded it.
        Assert.Equal(HttpStatusCode.Forbidden, release.StatusCode);
    }

    [Fact]
    public async Task Releasing_with_the_right_frees_the_window_again()
    {
        var w = await SeedAsync(canBlock: true, canRelease: true);
        using var _a = w.Api;
        using var _d = w.Dav;

        var uid = Guid.NewGuid().ToString();
        (await PutBlockAsync(w, uid)).EnsureSuccessStatusCode();
        (await PutBlockAsync(w, uid, "CANCELLED")).EnsureSuccessStatusCode();

        // Nothing was written to the bookings when the block was placed or cleared — suspension is derived —
        // so "the window is bookable again" is the only observable proof that revival happened.
        var start = new DateTimeOffset(2027, 6, 4, 10, 0, 0, TimeSpan.Zero);
        using var booking = await w.Api.PostAsJsonAsync($"/api/documents/{w.RoomId}/bookings",
            new { startsAt = start, endsAt = start.AddHours(1), purpose = "After the release" });

        Assert.True(booking.IsSuccessStatusCode, $"booking after release answered {booking.StatusCode}");
    }

    // The two actions the owner asked to be separate, so the trail can answer WHO cleared it to fly. Because
    // suspension is derived and therefore true only while the block stands, these events are the only durable
    // record that the flights were ever stopped.
    [Fact]
    public async Task Grounding_and_releasing_are_recorded_as_separate_actions()
    {
        var w = await SeedAsync(canBlock: true, canRelease: true);
        using var _a = w.Api;
        using var _d = w.Dav;

        var uid = Guid.NewGuid().ToString();
        (await PutBlockAsync(w, uid)).EnsureSuccessStatusCode();
        (await PutBlockAsync(w, uid, "CANCELLED")).EnsureSuccessStatusCode();

        var actions = (await TestJson.Get(w.Api, "/api/audit-events?limit=200")).GetProperty("events")
            .EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();

        Assert.Contains("Booking.Suspended", actions);
        Assert.Contains("Booking.Revived", actions);
    }

    // A suspended booking is SERVED as tentative while the stored document is untouched (ADR 0778). Ordered
    // deliberately: the booking is made FIRST, because one made into an existing block would be refused —
    // suspension is for commitments that already existed when the resource went out of service.
    [Fact]
    public async Task A_booking_caught_by_a_block_is_served_as_tentative()
    {
        var w = await SeedAsync(canBlock: true, canRelease: false);
        using var _a = w.Api;
        using var _d = w.Dav;

        var start = new DateTimeOffset(2027, 6, 4, 10, 0, 0, TimeSpan.Zero);
        var booking = await TestJson.Post(w.Api, $"/api/documents/{w.RoomId}/bookings",
            new { startsAt = start, endsAt = start.AddHours(1), purpose = "Caught by the block" });
        var bookingId = booking.GetProperty("id").GetGuid();

        (await PutBlockAsync(w, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();

        // Read from the LISTING, which is the surface that exists — there is no GET for a single booking,
        // and a test asserting against an invented address measures nothing.
        var reread = (await TestJson.Get(w.Api, $"/api/documents/{w.RoomId}/bookings"))
            .GetProperty("bookings").EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == bookingId);

        Assert.True(reread.GetProperty("suspended").GetBoolean(), "the booking did not report itself suspended");

        // ...and it is still Active, holding its slot. Collapsing the two would silently free the room.
        Assert.Equal("Active", reread.GetProperty("status").GetString());
    }
}
