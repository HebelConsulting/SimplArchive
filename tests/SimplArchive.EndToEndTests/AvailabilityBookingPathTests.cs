using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// The REAL path for the availability rule (#1124), which the invariant's own integration tests cannot reach:
// they add ResourceBooking rows directly, so they prove the rule against a fake. This drives what a user
// drives — publish a window in the Availability collection, then file an entry into the SCHEDULE — and is the
// only test that can say whether classification turns that entry into a booking at all.
//
// It exists because the first report of this feature not working was exactly that gap: the entry had gone
// into Availability rather than Schedule, so no booking was ever created and the invariant never ran, while
// every unit and integration test stayed green.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AvailabilityBookingPathTests
{
    private readonly E2EApiFactory _factory;

    public AvailabilityBookingPathTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_entry_filed_in_the_schedule_outside_a_published_window_is_refused()
    {
        var (api, collections) = await RoomCollectionsAsync();

        // Offered: 09:00–12:00.
        var offered = await api.PostAsJsonAsync($"/api/documents/{collections["Availability"]}/appointments", new
        {
            summary = "Open hours",
            start = new DateTime(2027, 6, 8, 9, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2027, 6, 8, 12, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(HttpStatusCode.Created, offered.StatusCode);

        // Requested: 11:00–13:00 — starts inside the window and ends outside it, which is the reported case.
        var outside = await api.PostAsJsonAsync($"/api/documents/{collections["Schedule"]}/appointments", new
        {
            summary = "Dinner",
            start = new DateTime(2027, 6, 8, 11, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2027, 6, 8, 13, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(HttpStatusCode.Conflict, outside.StatusCode);

        // ...and one wholly inside it still goes through, so the rule is not simply refusing everything.
        var inside = await api.PostAsJsonAsync($"/api/documents/{collections["Schedule"]}/appointments", new
        {
            summary = "Lunch",
            start = new DateTime(2027, 6, 8, 10, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2027, 6, 8, 11, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);

        api.Dispose();
    }

    private async Task<(HttpClient Api, Dictionary<string, Guid> Collections)> RoomCollectionsAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"avail-{Guid.NewGuid():N}@e2e.local";
        const string password = "availpath-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Availability", canManageRepositories: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Rooms {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var masks = (await TestJson.Get(api, "/api/masks")).GetProperty("masks").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetGuid());
        var roomId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
            new { name = $"Room {Guid.NewGuid():N}", maskId = masks["Meeting room"] })).GetProperty("id").GetGuid();

        var children = (await TestJson.Get(api, $"/api/documents/{roomId}/children")).GetProperty("children")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetGuid());

        return (api, children);
    }
}
