using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The inventory-booking primitive's HTTP edge (ADR 0735; endpoints interview 2026-09-03), driven over the
// real Api: a Meeting-room folder is bookable, POST on its `bookings` rel claims a slot (creating the Room
// booking document, the Schedule calendar and its appointment), an overlap answers 409 with its own error
// code, and cancelling frees the slot while keeping the record.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class BookingFlowTests
{
    private readonly E2EApiFactory _factory;

    public BookingFlowTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, Guid RepoId, Guid RoomId)> RoomAsync()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Rooms {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        // The room is created like any typed folder — through the children collection, carrying its mask id
        // (#678). The id comes from the masks listing, not a hardcoded constant: the listing is the contract.
        var masks = (await TestJson.Get(api, "/api/masks")).GetProperty("masks").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetGuid());
        var roomId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
            new { name = "Room 1", maskId = masks["Meeting room"] })).GetProperty("id").GetGuid();

        return (api, repoId, roomId);
    }

    private static object Slot(int startHour, int endHour, string? purpose = null) => new
    {
        startsAt = new DateTimeOffset(2027, 3, 10, startHour, 0, 0, TimeSpan.Zero),
        endsAt = new DateTimeOffset(2027, 3, 10, endHour, 0, 0, TimeSpan.Zero),
        purpose,
    };

    [Fact]
    public async Task The_bookings_rel_is_emitted_on_a_room_and_on_nothing_else()
    {
        var (api, repoId, roomId) = await RoomAsync();

        // ADR 0543: the rel's presence IS the affordance — emitted exactly where following it would work.
        var room = await TestJson.Get(api, $"/api/documents/{roomId}");
        Assert.Contains(room.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "bookings");

        var repo = await TestJson.Get(api, $"/api/documents/{repoId}");
        Assert.DoesNotContain(repo.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "bookings");

        api.Dispose();
    }

    [Fact]
    public async Task Booking_creates_the_document_the_schedule_and_the_appointment()
    {
        var (api, _, roomId) = await RoomAsync();

        var response = await api.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(10, 12, "Standup"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);

        var booking = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("Active", booking.GetProperty("status").GetString());
        Assert.Equal("Standup", booking.GetProperty("purpose").GetString());
        Assert.True(booking.GetProperty("canCancel").GetBoolean());
        Assert.Contains(booking.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "document");
        Assert.Contains(booking.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "cancel");

        // The room now holds the booking document AND the Schedule calendar; the calendar holds the
        // appointment — the CalDAV projection (endpoints interview: per-room calendar).
        var children = await ChildrenByNameAsync(api, roomId);
        Assert.Contains("Schedule", children.Keys);
        Assert.Contains(children.Keys, n => n.StartsWith("Booking 2027-03-10"));

        var scheduleChildren = await ChildrenByNameAsync(api, children["Schedule"]);
        Assert.Single(scheduleChildren);

        api.Dispose();
    }

    [Fact]
    public async Task An_overlapping_slot_answers_409_with_its_own_error_code()
    {
        var (api, _, roomId) = await RoomAsync();

        Assert.Equal(HttpStatusCode.Created,
            (await api.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(10, 12))).StatusCode);

        var refused = await api.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(11, 13));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
        // Its own code — never the blanket DOCUMENT_NAME_CONFLICT a generic catch would report.
        Assert.Equal("BOOKING_SLOT_CONFLICT", problem.GetProperty("errorCode").GetString());
        // The refusal names the occupied range — a rejection the caller can act on.
        Assert.Contains("overlaps", problem.GetProperty("detail").GetString());

        // [start, end): the slot ENDING where the taken one starts is the ordinary case, not a clash.
        Assert.Equal(HttpStatusCode.Created,
            (await api.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(8, 10))).StatusCode);

        api.Dispose();
    }

    [Fact]
    public async Task Cancelling_frees_the_slot_keeps_the_record_and_clears_the_calendar()
    {
        var (api, _, roomId) = await RoomAsync();

        var created = await api.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(14, 16));
        var etag = created.Headers.ETag!.Tag;
        var bookingId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetGuid();

        // No If-Match → 428, the repo-wide contract for mutations.
        using (var bare = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{roomId}/bookings/{bookingId}"))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, (await api.SendAsync(bare)).StatusCode);
        }

        using var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{roomId}/bookings/{bookingId}");
        cancel.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(cancel)).StatusCode);

        // The claim is history, not gone: the listing shows it Cancelled...
        var listing = await TestJson.Get(api, $"/api/documents/{roomId}/bookings");
        var row = listing.GetProperty("bookings").EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == bookingId);
        Assert.Equal("Cancelled", row.GetProperty("status").GetString());

        // ...the slot is free again...
        Assert.Equal(HttpStatusCode.Created,
            (await api.PostAsJsonAsync($"/api/documents/{roomId}/bookings", Slot(14, 16))).StatusCode);

        // ...and the first appointment left every subscribed calendar (soft-deleted), replaced by the
        // rebooking's own.
        var children = await ChildrenByNameAsync(api, roomId);
        var scheduleChildren = await ChildrenByNameAsync(api, children["Schedule"]);
        Assert.Single(scheduleChildren);

        api.Dispose();
    }

    private static async Task<Dictionary<string, Guid>> ChildrenByNameAsync(HttpClient api, Guid folderId)
    {
        var children = await TestJson.Get(api, $"/api/documents/{folderId}/children");
        return children.GetProperty("children").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetGuid());
    }
}
