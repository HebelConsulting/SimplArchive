using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// Slice 4b of #1091: the people on a booking are TOLD when their resource goes out of service, and told
// again when it comes back.
//
// The pair is the point. Suspension is derived (ADR 0778), so a pilot has no event to look at and nothing
// changes on their booking except an answer they would have to go and ask for. A notification is the only
// thing that reaches them — and a suspension notice with no revival notice is worse than neither, because
// they act on a fact that has since stopped being true.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class MaintenanceBlockNotificationTests
{
    private readonly E2EApiFactory _factory;

    public MaintenanceBlockNotificationTests(E2EApiFactory factory) => _factory = factory;

    private sealed record World(HttpClient Pilot, HttpClient Engineer, HttpClient Dav, AuthenticationHeaderValue Basic, Guid RoomId, string MaintenanceHref);

    // Two people on purpose: the PILOT books, the ENGINEER grounds. One person doing both would prove nothing
    // — the notification service never notifies the actor about their own action, so a single-user test would
    // pass while delivering nothing to anybody.
    private async Task<World> SeedAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var pilotEmail = $"pilot-{Guid.NewGuid():N}@e2e.local";
        var engineerEmail = $"eng-{Guid.NewGuid():N}@e2e.local";
        const string password = "mbn-1234";

        await _factory.SeedUserAsync(tenantId, pilotEmail, password, "Pilot", canManageRepositories: true);
        await _factory.SeedUserAsync(tenantId, engineerEmail, password, "Engineer",
            canManageRepositories: true, canBlockResources: true, canReleaseResources: true);

        var pilot = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(pilotEmail, password));
        var engineer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(engineerEmail, password));

        var repositoryId = (await TestJson.Post(pilot, "/api/repositories", new { name = $"mbn-{Guid.NewGuid():N}"[..18] }))
            .GetProperty("id").GetGuid();
        var roomName = $"Room {Guid.NewGuid():N}"[..14];
        var roomId = (await TestJson.Post(pilot, $"/api/documents/{repositoryId}/children", new { name = roomName }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(pilot, $"/api/documents/{roomId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });

        var scheduleId = await ProvisionedCollectionAsync(pilot, roomId, "Schedule");
        var maintenanceId = await ProvisionedCollectionAsync(pilot, roomId, "Maintenance");

        // The engineer needs to be able to WRITE into the Maintenance collection; the repository was made by
        // the pilot, so a grant is required before the right is even reached.
        await TestJson.Put(pilot, $"/api/documents/{repositoryId}/acl-entries/users/{await UserIdAsync(engineer)}",
            new { canSee = true, canReadContent = true, canEditContent = true, canCreateSubItems = true });

        var davPassword = (await TestJson.Post(engineer, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{engineerEmail}:{davPassword}")));
        var dav = _factory.CreateClient();

        return new World(pilot, engineer, dav, basic, roomId, await MaintenanceHrefAsync(dav, basic, roomName));
    }

    private static async Task<Guid> UserIdAsync(HttpClient client) =>
        (await TestJson.Get(client, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid();

    private static async Task<string> MaintenanceHrefAsync(HttpClient dav, AuthenticationHeaderValue basic, string roomName)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/caldav/calendars/") { Headers = { Authorization = basic } };
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>", Encoding.UTF8, "text/xml");
        using var home = await dav.SendAsync(request);
        var body = await home.Content.ReadAsStringAsync();
        var block = System.Text.RegularExpressions.Regex
            .Split(body, "</[a-zA-Z]+:response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .First(b => b.Contains($"{roomName} / Maintenance", StringComparison.Ordinal));
        return System.Text.RegularExpressions.Regex.Match(block, "<[^>]*href[^>]*>([^<]+)</").Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PutBlockAsync(World w, string uid, string status = "CONFIRMED")
    {
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{w.MaintenanceHref}{uid}.ics") { Headers = { Authorization = w.Basic } };
        put.Content = new StringContent(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//e2e//EN\r\nBEGIN:VEVENT\r\n"
            + $"UID:{uid}\r\nDTSTAMP:20270701T090000Z\r\nDTSTART:20270704T080000Z\r\nDTEND:20270704T180000Z\r\n"
            + $"STATUS:{status}\r\nSUMMARY:50h check\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
            Encoding.UTF8, "text/calendar");
        return await w.Dav.SendAsync(put);
    }

    private static async Task<List<string>> PilotNotificationTypesAsync(World w) =>
        [.. (await TestJson.Get(w.Pilot, "/api/notifications?limit=100")).GetProperty("notifications")
            .EnumerateArray().Select(n => n.GetProperty("type").GetString() ?? string.Empty)];

    private static async Task BookAsync(World w) =>
        await TestJson.Post(w.Pilot, $"/api/documents/{w.RoomId}/bookings", new
        {
            startsAt = new DateTimeOffset(2027, 7, 4, 10, 0, 0, TimeSpan.Zero),
            endsAt = new DateTimeOffset(2027, 7, 4, 11, 0, 0, TimeSpan.Zero),
            purpose = "Training flight",
        });

    [Fact]
    public async Task The_booker_is_told_when_the_resource_goes_out_of_service()
    {
        var w = await SeedAsync();
        using var _p = w.Pilot;
        using var _e = w.Engineer;
        using var _d = w.Dav;

        await BookAsync(w);
        (await PutBlockAsync(w, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();

        Assert.Contains("BookingSuspended", await PilotNotificationTypesAsync(w));
    }

    [Fact]
    public async Task The_booker_is_told_again_when_it_comes_back()
    {
        var w = await SeedAsync();
        using var _p = w.Pilot;
        using var _e = w.Engineer;
        using var _d = w.Dav;

        await BookAsync(w);
        var uid = Guid.NewGuid().ToString();
        (await PutBlockAsync(w, uid)).EnsureSuccessStatusCode();
        (await PutBlockAsync(w, uid, "CANCELLED")).EnsureSuccessStatusCode();

        var types = await PilotNotificationTypesAsync(w);
        Assert.Contains("BookingSuspended", types);
        Assert.Contains("BookingRevived", types);
    }

    // A block that catches nothing tells nobody. Worth pinning because the cheap implementation — notify
    // everyone with a booking on the resource — reads as correct and would spam every pilot on the aircraft
    // about a window none of them had booked.
    [Fact]
    public async Task A_block_that_catches_no_booking_notifies_nobody()
    {
        var w = await SeedAsync();
        using var _p = w.Pilot;
        using var _e = w.Engineer;
        using var _d = w.Dav;

        // No booking at all — the block is placed against an empty schedule.
        (await PutBlockAsync(w, Guid.NewGuid().ToString())).EnsureSuccessStatusCode();

        Assert.DoesNotContain("BookingSuspended", await PilotNotificationTypesAsync(w));
    }

    /// <summary>The collection a bookable resource was PROVISIONED with (#1097), found by name.</summary>
    /// <remarks>
    /// These tests used to create Schedule and Maintenance themselves, because nothing did. Assigning a
    /// bookable mask now provisions all three, so creating one here would collide with the provisioned
    /// folder on the sibling-name invariant — and, worse, a test that still made its own would be testing
    /// its fixture rather than what a real resource looks like.
    /// </remarks>
    private static async Task<Guid> ProvisionedCollectionAsync(HttpClient api, Guid resourceId, string name)
    {
        var listing = await TestJson.Get(api, $"/api/documents/{resourceId}/children");
        var key = listing.TryGetProperty("items", out _) ? "items" : "children";
        return listing.GetProperty(key).EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == name)
            .GetProperty("id").GetGuid();
    }
}
