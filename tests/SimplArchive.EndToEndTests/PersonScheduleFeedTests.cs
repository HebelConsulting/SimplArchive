using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// A person's own time as a CalDAV collection (ADR 0775): a pilot subscribes and sees every flight they are on,
// wherever the flight itself is filed.
//
// The booking is ONE document living in the resource's Schedule (ADR 0774), so the people on it hold claims
// and no documents — their calendars have nothing to enumerate, and this composes them. Only a wire test can
// show the composition, the read-only refusals, and that the collection is the caller's alone.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class PersonScheduleFeedTests
{
    private readonly E2EApiFactory _factory;

    public PersonScheduleFeedTests(E2EApiFactory factory) => _factory = factory;

    private const string CalDavRoot = "/caldav";

    private sealed record Setup(
        HttpClient Api, HttpClient Dav, AuthenticationHeaderValue Basic, Guid TenantId, Guid UserId,
        Guid PersonId, string PersonName, Guid BookingDocumentId);

    /// <summary>
    /// A room booked through the ordinary endpoint, plus a second bookable document standing for the caller
    /// with a claim on that same booking.
    /// </summary>
    /// <remarks>
    /// The second claim and the `ResourcePrincipal` are written directly, because nothing in the core writes
    /// either yet: the API creates single-resource bookings, and declaring that a document represents a person
    /// is the module seam that arrives with the flight-school slice. That is the same deliberate order slice 1
    /// used — the primitive lands before its writer, and this is what proves the primitive.
    /// </remarks>
    private async Task<Setup> SeedAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"sched-{Guid.NewGuid():N}@e2e.local";
        const string password = "sched-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Pilot", canManageRepositories: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repositoryId = (await TestJson.Post(api, "/api/repositories", new { name = $"sched-{Guid.NewGuid():N}"[..18] }))
            .GetProperty("id").GetGuid();

        // The holding resource: a meeting room, whose mask is bookable and whose Schedule is created for it.
        var roomId = (await TestJson.Post(api, $"/api/documents/{repositoryId}/children", new { name = $"Room {Guid.NewGuid():N}"[..14] }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{roomId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });

        var start = new DateTimeOffset(2027, 4, 8, 9, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(2);
        var booking = await TestJson.Post(api, $"/api/documents/{roomId}/bookings",
            new { startsAt = start, endsAt = end, purpose = "Lesson 4" });
        var bookingDocumentId = booking.GetProperty("id").GetGuid();

        // The person: a second bookable document, which the claim's invariant requires.
        var personName = $"Pilot {Guid.NewGuid():N}"[..14];
        var personId = (await TestJson.Post(api, $"/api/documents/{repositoryId}/children", new { name = personName }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(api, $"/api/documents/{personId}/mask", new { maskId = WellKnownMaskIds.MeetingRoom });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var actualBookingDocumentId = await db.ResourceBookings.IgnoreQueryFilters()
                .Where(b => b.ResourceDocumentId == roomId)
                .Select(b => b.BookingDocumentId)
                .SingleAsync();

            db.ResourcePrincipals.Add(new ResourcePrincipal
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ResourceDocumentId = personId,
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.ResourceBookings.Add(new ResourceBooking
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ResourceDocumentId = personId,
                BookingDocumentId = actualBookingDocumentId,
                StartsAtUtc = start,
                EndsAtUtc = end,
                Status = BookingStatus.Active,
                BookedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            bookingDocumentId = actualBookingDocumentId;
        }

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return new Setup(api, _factory.CreateClient(), basic, tenantId, userId, personId, personName, bookingDocumentId);
    }

    private static async Task<string> PropfindAsync(HttpClient dav, AuthenticationHeaderValue basic, string path, int depth)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = basic } };
        request.Headers.Add("Depth", depth.ToString());
        request.Content = new StringContent(
            "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>", Encoding.UTF8, "text/xml");
        using var response = await dav.SendAsync(request);
        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static List<string> Hrefs(string multistatus) =>
        [.. System.Text.RegularExpressions.Regex.Matches(multistatus, "<[^>]*href[^>]*>([^<]+)</")
            .Select(m => m.Groups[1].Value)];

    [Fact]
    public async Task The_persons_schedule_is_offered_and_lists_the_flight_they_are_claimed_for()
    {
        var s = await SeedAsync();
        using var _a = s.Api;
        using var _d = s.Dav;

        var home = await PropfindAsync(s.Dav, s.Basic, $"{CalDavRoot}/calendars/", depth: 1);
        Assert.Contains(s.PersonName, home);

        var listing = await PropfindAsync(s.Dav, s.Basic, ScheduleHrefOf(home, s.PersonName), depth: 1);
        var items = Hrefs(listing).Where(h => h.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)).ToList();

        // One member: the booking the person holds a claim on — a document filed in the ROOM's Schedule,
        // which is exactly what the person's own calendar cannot enumerate by parent.
        Assert.Single(items);
    }

    [Fact]
    public async Task The_item_serves_the_bookings_own_bytes()
    {
        var s = await SeedAsync();
        using var _a = s.Api;
        using var _d = s.Dav;

        var home = await PropfindAsync(s.Dav, s.Basic, $"{CalDavRoot}/calendars/", depth: 1);
        var listing = await PropfindAsync(s.Dav, s.Basic, ScheduleHrefOf(home, s.PersonName), depth: 1);
        var itemHref = Hrefs(listing).First(h => h.EndsWith(".ics", StringComparison.OrdinalIgnoreCase));

        using var get = new HttpRequestMessage(HttpMethod.Get, itemHref) { Headers = { Authorization = s.Basic } };
        using var response = await s.Dav.SendAsync(get);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // Real stored bytes, not something composed for this collection: the same .ics the room's Schedule
        // serves. A client must not be able to tell the two apart — it is one booking seen from two sides.
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains("BEGIN:VEVENT", body);
    }

    // Read-only by design, not omission: a booking is made by writing into the resource's Schedule, where the
    // conflict check lives. Accepting a write here would be a second entrance to creating one.
    [Fact]
    public async Task Writing_into_a_person_schedule_is_refused()
    {
        var s = await SeedAsync();
        using var _a = s.Api;
        using var _d = s.Dav;

        var home = await PropfindAsync(s.Dav, s.Basic, $"{CalDavRoot}/calendars/", depth: 1);
        var listing = await PropfindAsync(s.Dav, s.Basic, ScheduleHrefOf(home, s.PersonName), depth: 1);
        var itemHref = Hrefs(listing).First(h => h.EndsWith(".ics", StringComparison.OrdinalIgnoreCase));

        using var put = new HttpRequestMessage(HttpMethod.Put, itemHref) { Headers = { Authorization = s.Basic } };
        put.Content = new StringContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", Encoding.UTF8, "text/calendar");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Dav.SendAsync(put)).StatusCode);

        // DELETE is the one that matters most: unguarded it would reach the ACL walk with a collection id that
        // has no document behind it, which throws — a 500 where the honest answer is "you may not".
        using var delete = new HttpRequestMessage(HttpMethod.Delete, itemHref) { Headers = { Authorization = s.Basic } };
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Dav.SendAsync(delete)).StatusCode);
    }

    // The collection id is derived from the resource document, so a URL that leaked once must not resolve for
    // anyone else — the id is scoped to the caller's OWN resources when it is recognised.
    [Fact]
    public async Task Another_users_schedule_does_not_resolve_for_this_caller()
    {
        var mine = await SeedAsync();
        using var _a = mine.Api;
        using var _d = mine.Dav;
        var theirs = await SeedAsync();
        using var _a2 = theirs.Api;
        using var _d2 = theirs.Dav;

        var theirHome = await PropfindAsync(theirs.Dav, theirs.Basic, $"{CalDavRoot}/calendars/", depth: 1);
        var theirSchedule = ScheduleHrefOf(theirHome, theirs.PersonName);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), theirSchedule) { Headers = { Authorization = mine.Basic } };
        request.Headers.Add("Depth", "0");
        using var response = await mine.Dav.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.MultiStatus, response.StatusCode);
    }

    /// <summary>The collection href whose display name is EXACTLY this.</summary>
    /// <remarks>
    /// Exactly, not "contains": a bookable document also has a real Schedule folder, and DavTree names a
    /// folder "{parent} / {folder}" — so "Pilot 4f2a" matches the empty "Pilot 4f2a / Schedule" as readily as
    /// the computed feed. Written loosely first, this found the wrong collection and every assertion after it
    /// failed for a reason that had nothing to do with the feed.
    /// </remarks>
    private static string ScheduleHrefOf(string multistatus, string displayName)
    {
        // Split case-INSENSITIVELY: this server emits "</d:response>" and splitting on "</D:response>" does
        // not split at all — the whole document then becomes one "block", it contains every name, and the
        // first href in it is the home set itself. Every assertion downstream then ran against the home set
        // and failed for reasons that had nothing to do with the feed.
        var blocks = System.Text.RegularExpressions.Regex.Split(
            multistatus, "</[a-zA-Z]+:response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var block = blocks.FirstOrDefault(b =>
            System.Text.RegularExpressions.Regex.IsMatch(b, $"displayname[^>]*>{System.Text.RegularExpressions.Regex.Escape(displayName)}</"))
            ?? throw new InvalidOperationException($"No collection named exactly '{displayName}' in the home set.");
        return Hrefs(block).First();
    }
}
