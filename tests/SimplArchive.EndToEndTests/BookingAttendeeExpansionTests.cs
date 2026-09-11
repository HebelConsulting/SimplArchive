using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// ATTENDEE expansion (ADR 0776): one PUT into a resource's Schedule naming an attendee books BOTH — the
// resource and the person — or refuses the whole write.
//
// This is the slice where a multi-claim booking is finally created the way a user would create one, so it is
// also the first test of ADR 0774's shape against a real client rather than rows inserted by hand.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class BookingAttendeeExpansionTests
{
    private readonly E2EApiFactory _factory;

    public BookingAttendeeExpansionTests(E2EApiFactory factory) => _factory = factory;

    private const string CalDavRoot = "/caldav";

    private sealed record World(
        HttpClient Api, HttpClient Dav, AuthenticationHeaderValue Basic, Guid TenantId,
        Guid RoomId, string RoomName, Guid PersonId, string PersonEmail);

    /// <summary>A bookable room with its Schedule, and a second bookable document representing a user.</summary>
    private async Task<World> SeedAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"att-{Guid.NewGuid():N}@e2e.local";
        const string password = "attend-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Attendee", canManageRepositories: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repositoryId = (await TestJson.Post(api, "/api/repositories", new { name = $"att-{Guid.NewGuid():N}"[..18] }))
            .GetProperty("id").GetGuid();

        var roomName = $"Room {Guid.NewGuid():N}"[..14];
        var roomId = (await TestJson.Post(api, $"/api/documents/{repositoryId}/children", new { name = roomName }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{roomId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });

        // The Schedule is created explicitly, because assigning the MeetingRoom mask does NOT create it — the
        // bookings endpoint does, lazily, on the first booking. So a room booked only through calendar clients
        // has no collection to PUT into until somebody books it in the app once (noted in ADR 0776).
        var scheduleId = await ProvisionedCollectionAsync(api, roomId, "Schedule");

        var personId = (await TestJson.Post(api, $"/api/documents/{repositoryId}/children", new { name = $"Person {Guid.NewGuid():N}"[..16] }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{personId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.ResourcePrincipals.Add(new ResourcePrincipal
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ResourceDocumentId = personId,
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return new World(api, _factory.CreateClient(), basic, tenantId, roomId, roomName, personId, email);
    }

    private static string Ics(string uid, string? attendee, string start = "20270408T090000Z", string end = "20270408T110000Z") =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//e2e//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20270401T090000Z\r\nDTSTART:{start}\r\nDTEND:{end}\r\nSUMMARY:Lesson\r\n"
        + (attendee is null ? string.Empty : $"ATTENDEE:mailto:{attendee}\r\n")
        + "END:VEVENT\r\nEND:VCALENDAR\r\n";

    /// <summary>The room's Schedule collection href, from the caller's calendar home set.</summary>
    private static async Task<string> ScheduleHrefAsync(HttpClient dav, AuthenticationHeaderValue basic, string roomName)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"{CalDavRoot}/calendars/") { Headers = { Authorization = basic } };
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>", Encoding.UTF8, "text/xml");
        using var response = await dav.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Split case-insensitively: this server emits lowercase element names, and splitting on "</D:response>"
        // silently yields one block containing everything.
        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);

        var blocks = System.Text.RegularExpressions.Regex.Split(body, "</[a-zA-Z]+:response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var block = blocks.FirstOrDefault(b => b.Contains($"{roomName} / Schedule", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No '{roomName} / Schedule' in the home set. Body: {body}");
        return System.Text.RegularExpressions.Regex.Match(block, "<[^>]*href[^>]*>([^<]+)</").Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient dav, AuthenticationHeaderValue basic, string href, string ics)
    {
        using var put = new HttpRequestMessage(HttpMethod.Put, href) { Headers = { Authorization = basic } };
        put.Content = new StringContent(ics, Encoding.UTF8, "text/calendar");
        return await dav.SendAsync(put);
    }

    private async Task<List<ResourceBooking>> ClaimsAsync(Guid tenantId, Guid roomId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var documentIds = await db.ResourceBookings.IgnoreQueryFilters()
            .Where(b => b.TenantId == tenantId && b.ResourceDocumentId == roomId)
            .Select(b => b.BookingDocumentId)
            .ToListAsync();

        return await db.ResourceBookings.IgnoreQueryFilters()
            .Where(b => documentIds.Contains(b.BookingDocumentId))
            .ToListAsync();
    }

    [Fact]
    public async Task An_attendee_becomes_a_second_claim_on_the_same_booking()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var schedule = await ScheduleHrefAsync(w.Dav, w.Basic, w.RoomName);
        var uid = Guid.NewGuid().ToString();
        (await PutAsync(w.Dav, w.Basic, $"{schedule}{uid}.ics", Ics(uid, w.PersonEmail))).EnsureSuccessStatusCode();

        var claims = await ClaimsAsync(w.TenantId, w.RoomId);

        // One document, two claims — the aircraft and the person — with one window between them.
        Assert.Equal(2, claims.Count);
        Assert.Single(claims.Select(c => c.BookingDocumentId).Distinct());
        Assert.Contains(claims, c => c.ResourceDocumentId == w.RoomId);
        Assert.Contains(claims, c => c.ResourceDocumentId == w.PersonId);
        Assert.Single(claims.Select(c => (c.StartsAtUtc, c.EndsAtUtc)).Distinct());
    }

    // The control: the same PUT without an attendee books the room alone, so the test above cannot be passing
    // because something creates a second claim regardless.
    [Fact]
    public async Task Without_an_attendee_only_the_resource_is_claimed()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var schedule = await ScheduleHrefAsync(w.Dav, w.Basic, w.RoomName);
        var uid = Guid.NewGuid().ToString();
        (await PutAsync(w.Dav, w.Basic, $"{schedule}{uid}.ics", Ics(uid, attendee: null))).EnsureSuccessStatusCode();

        Assert.Single(await ClaimsAsync(w.TenantId, w.RoomId));
    }

    // Refused, not ignored. Dropping the attendee would book the room alone while the author believes a second
    // participant is coming — a booking that looks complete to everyone and is not.
    [Fact]
    public async Task An_attendee_the_archive_cannot_book_refuses_the_whole_write()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var schedule = await ScheduleHrefAsync(w.Dav, w.Basic, w.RoomName);
        var uid = Guid.NewGuid().ToString();
        var response = await PutAsync(w.Dav, w.Basic, $"{schedule}{uid}.ics", Ics(uid, "nobody@e2e.local"));

        Assert.False(response.IsSuccessStatusCode);

        // And nothing was booked: the refusal is of the WRITE, not merely of the attendee.
        Assert.Empty(await ClaimsAsync(w.TenantId, w.RoomId));
    }

    // Editing a multi-claim booking is a rebooking of every claim. Moving one claim's slot while its siblings
    // kept the old one would fail ADR 0774's same-window invariant, so this is what stops an edit being
    // refused by an invariant the edit itself created.
    [Fact]
    public async Task Moving_the_event_moves_every_claim()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var schedule = await ScheduleHrefAsync(w.Dav, w.Basic, w.RoomName);
        var uid = Guid.NewGuid().ToString();
        var href = $"{schedule}{uid}.ics";
        (await PutAsync(w.Dav, w.Basic, href, Ics(uid, w.PersonEmail))).EnsureSuccessStatusCode();

        (await PutAsync(w.Dav, w.Basic, href, Ics(uid, w.PersonEmail, "20270408T140000Z", "20270408T160000Z")))
            .EnsureSuccessStatusCode();

        var claims = (await ClaimsAsync(w.TenantId, w.RoomId)).Where(c => c.Status == BookingStatus.Active).ToList();
        Assert.Equal(2, claims.Count);
        Assert.Single(claims.Select(c => c.StartsAtUtc).Distinct());
        Assert.Equal(14, claims[0].StartsAtUtc.UtcDateTime.Hour);
    }

    // Removed from the attendee list is removed from the flight: the claim is cancelled, freeing that person's
    // slot, while the booking itself stands.
    [Fact]
    public async Task Dropping_an_attendee_cancels_their_claim_and_leaves_the_booking()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var schedule = await ScheduleHrefAsync(w.Dav, w.Basic, w.RoomName);
        var uid = Guid.NewGuid().ToString();
        var href = $"{schedule}{uid}.ics";
        (await PutAsync(w.Dav, w.Basic, href, Ics(uid, w.PersonEmail))).EnsureSuccessStatusCode();
        (await PutAsync(w.Dav, w.Basic, href, Ics(uid, attendee: null))).EnsureSuccessStatusCode();

        var claims = await ClaimsAsync(w.TenantId, w.RoomId);
        Assert.Equal(BookingStatus.Active, claims.Single(c => c.ResourceDocumentId == w.RoomId).Status);
        Assert.Equal(BookingStatus.Cancelled, claims.Single(c => c.ResourceDocumentId == w.PersonId).Status);
    }

    // The person's own time is claimed, so a second flight in the same hour is refused even on a different
    // resource — "an instructor cannot teach two students at once", enforced by the invariant that already
    // said "a room cannot be in two places at once".
    [Fact]
    public async Task A_person_claimed_by_one_booking_cannot_be_claimed_by_another_at_the_same_time()
    {
        var w = await SeedAsync();
        using var _a = w.Api;
        using var _d = w.Dav;

        var schedule = await ScheduleHrefAsync(w.Dav, w.Basic, w.RoomName);
        var first = Guid.NewGuid().ToString();
        (await PutAsync(w.Dav, w.Basic, $"{schedule}{first}.ics", Ics(first, w.PersonEmail))).EnsureSuccessStatusCode();

        // A second room, so the ROOM is free — only the person is not.
        var repositoryId = (await TestJson.Get(w.Api, $"/api/documents/{w.RoomId}")).GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "parent").GetProperty("href").GetString()!;
        var otherRoomName = $"Room {Guid.NewGuid():N}"[..14];
        var otherRoomId = (await TestJson.Post(w.Api, $"{repositoryId}/children", new { name = otherRoomName }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(w.Api, $"/api/documents/{otherRoomId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });
        var otherSchedule = await ScheduleHrefAsync(w.Dav, w.Basic, otherRoomName);
        var second = Guid.NewGuid().ToString();
        var response = await PutAsync(w.Dav, w.Basic, $"{otherSchedule}{second}.ics", Ics(second, w.PersonEmail));

        Assert.False(response.IsSuccessStatusCode);
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
