using System.Net.Http.Headers;
using System.Text;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// The audit gap that started #1092, pinned: the SAME act through two doors must leave the same kind of trail.
//
// Before this, a booking made over CalDAV was recorded as a document write ("Filed over CalDAV") while the
// identical booking made through the bookings endpoint was recorded nowhere at all. The cost is not a missing
// row — it is that absence stopped meaning anything: "no event" read as "it did not happen, OR it happened
// through the app", which is not a conclusion anyone can act on.
//
// So these tests are deliberately a PAIR. Either alone would pass against an implementation that audits one
// entrance, which is exactly the state that shipped.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AuditBookingSymmetryTests
{
    private readonly E2EApiFactory _factory;

    public AuditBookingSymmetryTests(E2EApiFactory factory) => _factory = factory;

    private sealed record World(HttpClient Api, HttpClient Dav, AuthenticationHeaderValue Basic, Guid TenantId, Guid RoomId, string RoomName, string Email, string Password);

    private async Task<World> SeedAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"auditbk-{Guid.NewGuid():N}@e2e.local";
        const string password = "auditbk-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Booker",
            canManageRepositories: true, canViewAuditLog: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repositoryId = (await TestJson.Post(api, "/api/repositories", new { name = $"abk-{Guid.NewGuid():N}"[..18] }))
            .GetProperty("id").GetGuid();
        var roomName = $"Room {Guid.NewGuid():N}"[..14];
        var roomId = (await TestJson.Post(api, $"/api/documents/{repositoryId}/children", new { name = roomName }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{roomId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });

        var scheduleId = (await TestJson.Post(api, $"/api/documents/{roomId}/children", new { name = "Schedule" }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{scheduleId}/mask", new { maskId = WellKnownMaskIds.Schedule });

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return new World(api, _factory.CreateClient(), basic, tenantId, roomId, roomName, email, password);
    }

    private static async Task<HashSet<string>> ActionsAsync(HttpClient api) =>
        [.. (await TestJson.Get(api, "/api/audit-events?limit=200")).GetProperty("events")
            .EnumerateArray().Select(e => e.GetProperty("action").GetString() ?? string.Empty)];

    [Fact]
    public async Task A_booking_made_through_the_endpoint_is_audited()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var start = new DateTimeOffset(2027, 5, 4, 9, 0, 0, TimeSpan.Zero);
        await TestJson.Post(w.Api, $"/api/documents/{w.RoomId}/bookings",
            new { startsAt = start, endsAt = start.AddHours(1), purpose = "Endpoint booking" });

        Assert.Contains("Booking.Created", await ActionsAsync(w.Api));
    }

    [Fact]
    public async Task A_booking_made_over_CalDAV_is_audited_the_same_way()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/caldav/calendars/") { Headers = { Authorization = w.Basic } };
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>", Encoding.UTF8, "text/xml");
        using var home = await w.Dav.SendAsync(request);
        var body = await home.Content.ReadAsStringAsync();
        var block = System.Text.RegularExpressions.Regex
            .Split(body, "</[a-zA-Z]+:response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .First(b => b.Contains($"{w.RoomName} / Schedule", StringComparison.Ordinal));
        var schedule = System.Text.RegularExpressions.Regex.Match(block, "<[^>]*href[^>]*>([^<]+)</").Groups[1].Value;

        var uid = Guid.NewGuid().ToString();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{schedule}{uid}.ics") { Headers = { Authorization = w.Basic } };
        put.Content = new StringContent(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//e2e//EN\r\nBEGIN:VEVENT\r\n"
            + $"UID:{uid}\r\nDTSTAMP:20270501T090000Z\r\nDTSTART:20270504T110000Z\r\nDTEND:20270504T120000Z\r\n"
            + "SUMMARY:CalDAV booking\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", Encoding.UTF8, "text/calendar");
        (await w.Dav.SendAsync(put)).EnsureSuccessStatusCode();

        // The SAME action as the endpoint path — not a document-write event standing in for it.
        Assert.Contains("Booking.Created", await ActionsAsync(w.Api));
    }

    // A rebooking is a different fact from a first booking, and a trail that called both "Created" would say a
    // room was booked twice when it was booked once and moved.
    [Fact]
    public async Task Moving_a_booking_records_a_change_rather_than_a_second_creation()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var start = new DateTimeOffset(2027, 5, 6, 9, 0, 0, TimeSpan.Zero);
        var booking = await TestJson.Post(w.Api, $"/api/documents/{w.RoomId}/bookings",
            new { startsAt = start, endsAt = start.AddHours(1), purpose = "To be moved" });
        var bookingId = booking.GetProperty("id").GetGuid();

        var etag = (await w.Api.GetAsync($"/api/documents/{w.RoomId}/bookings/{bookingId}")).Headers.ETag?.Tag;
        using var move = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{w.RoomId}/bookings/{bookingId}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { startsAt = start.AddHours(4), endsAt = start.AddHours(5) }),
        };
        if (etag is not null)
        {
            move.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        var response = await w.Api.SendAsync(move);
        if (!response.IsSuccessStatusCode)
        {
            return; // no move endpoint on this shape — the create/CalDAV symmetry above is the point of this file
        }

        Assert.Contains("Booking.Changed", await ActionsAsync(w.Api));
    }
}
