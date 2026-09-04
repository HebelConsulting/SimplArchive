using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// ADR 0744's write-path symmetry, driven the way a calendar app does it: a room's Schedule is a CalDAV
// collection whose PUT is a conflict-checked BOOKING, an event edit is a rebooking (refused into a taken
// slot, moved into a free one), a recurring event is refused with its own code, a refused create leaves no
// husk behind to 409 the client's retry, and DELETE cancels the claim.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class BookingSymmetryTests
{
    private readonly E2EApiFactory _factory;

    public BookingSymmetryTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(
        HttpClient OwnerApi, HttpClient Dav, AuthenticationHeaderValue DavAuth, string ScheduleHref, Guid RoomId);

    // A room whose Schedule exists (the owner filed a 10–12 booking through the API), plus a second user
    // with inherited rights on the repo and a DAV password — the phone-with-a-calendar-app persona.
    private async Task<Rig> RigAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Rooms {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var masks = (await TestJson.Get(owner, "/api/masks")).GetProperty("masks").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetGuid());
        var roomId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = "Room 1", maskId = masks["Meeting room"] })).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created,
            (await owner.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(10, 12))).StatusCode);

        var email = $"booker-{Guid.NewGuid():N}@e2e.local";
        const string password = "booker-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Booker");
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{userId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true, canDelete = true });

        var userApi = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var generated = await TestJson.Post(userApi, "/api/me/webdav-password", new { });
        var davAuth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{generated.GetProperty("password").GetString()}")));
        userApi.Dispose();

        var children = await TestJson.Get(owner, $"/api/documents/{roomId}/children");
        var scheduleId = children.GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Schedule").GetProperty("id").GetGuid();

        return new Rig(owner, _factory.CreateClient(), davAuth, $"/caldav/calendars/{scheduleId}/", roomId);
    }

    private static object Slot(int startHour, int endHour) => new
    {
        startsAt = new DateTimeOffset(2027, 3, 10, startHour, 0, 0, TimeSpan.Zero),
        endsAt = new DateTimeOffset(2027, 3, 10, endHour, 0, 0, TimeSpan.Zero),
    };

    private static string Event(string uid, int startHour, int endHour, string? rrule = null) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SimplArchive//E2E//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\n"
        + $"DTSTAMP:20270301T090000Z\r\nDTSTART:20270310T{startHour:00}0000Z\r\nDTEND:20270310T{endHour:00}0000Z\r\n"
        + (rrule is null ? string.Empty : $"RRULE:{rrule}\r\n")
        + "SUMMARY:Synced booking\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static async Task<HttpResponseMessage> PutAsync(Rig rig, string uid, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{rig.ScheduleHref}{uid}.ics")
        {
            Headers = { Authorization = rig.DavAuth },
            Content = new StringContent(body, Encoding.UTF8, "text/calendar"),
        };
        return await rig.Dav.SendAsync(request);
    }

    private async Task<List<(string Status, string Start)>> BookingsAsync(Rig rig)
    {
        var listing = await TestJson.Get(rig.OwnerApi, $"/api/documents/{rig.RoomId}/bookings");
        return listing.GetProperty("bookings").EnumerateArray()
            .Select(b => (b.GetProperty("status").GetString()!, b.GetProperty("startsAt").GetDateTimeOffset().ToUniversalTime().ToString("HH:mm")))
            .ToList();
    }

    [Fact]
    public async Task A_conflicting_put_is_refused_and_leaves_no_husk_behind()
    {
        var rig = await RigAsync();
        var uid = $"sym-{Guid.NewGuid():N}";

        // Overlaps the owner's 10–12 booking: refused with the booking's own status, and NOT booked.
        var refused = await PutAsync(rig, uid, Event(uid, 11, 13));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Single(await BookingsAsync(rig));

        // The refusal left nothing at the resource name: the client's retry with a corrected time — the
        // very next thing every calendar app does — books instead of tripping over a stranded husk.
        var retried = await PutAsync(rig, uid, Event(uid, 13, 14));
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(2, (await BookingsAsync(rig)).Count);

        rig.OwnerApi.Dispose();
        rig.Dav.Dispose();
    }

    [Fact]
    public async Task An_event_edit_is_a_rebooking_and_a_recurring_event_is_refused()
    {
        var rig = await RigAsync();
        var uid = $"sym-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(rig, uid, Event(uid, 14, 15))).StatusCode);

        // Edited into the owner's slot: refused, and the claim stays where it was.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig, uid, Event(uid, 11, 12))).StatusCode);
        Assert.Contains(("Active", "14:00"), await BookingsAsync(rig));

        // Edited into a free slot: the claim MOVES — the row follows the .ics (ADR 0744's refresh).
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(rig, uid, Event(uid, 15, 16))).StatusCode);
        var bookings = await BookingsAsync(rig);
        Assert.Contains(("Active", "15:00"), bookings);
        Assert.DoesNotContain(("Active", "14:00"), bookings);

        // A repeating rule cannot claim one slot: refused, with nothing changed.
        var recurringUid = $"sym-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(rig, recurringUid, Event(recurringUid, 17, 18, rrule: "FREQ=WEEKLY"))).StatusCode);

        rig.OwnerApi.Dispose();
        rig.Dav.Dispose();
    }

    [Fact]
    public async Task A_dav_delete_cancels_the_claim_and_frees_the_slot()
    {
        var rig = await RigAsync();
        var uid = $"sym-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(rig, uid, Event(uid, 14, 15))).StatusCode);

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"{rig.ScheduleHref}{uid}.ics")
        {
            Headers = { Authorization = rig.DavAuth },
        })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await rig.Dav.SendAsync(delete)).StatusCode);
        }

        // The row went Cancelled through the SaveChanges sync — no DAV code knows about bookings — and
        // the slot is bookable again.
        Assert.Contains(("Cancelled", "14:00"), await BookingsAsync(rig));
        Assert.Equal(HttpStatusCode.Created,
            (await rig.OwnerApi.PostAsJsonAsync($"/api/documents/{rig.RoomId}/bookings", Slot(14, 15))).StatusCode);

        rig.OwnerApi.Dispose();
        rig.Dav.Dispose();
    }
}
