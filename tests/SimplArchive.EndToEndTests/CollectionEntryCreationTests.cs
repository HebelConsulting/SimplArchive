using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Every calendar-shaped collection can be listed and added to — not just the two the code happened to name.
//
// Reported from use while testing a release: "how can I add availability now? No context-menu, no buttons on
// calendar tab." It was not a client gap. Two independent places listed the calendar-shaped masks by hand and
// both said Calendar and Schedule, neither having been updated when Maintenance (ADR 0778) and Availability
// (ADR 0780) arrived:
//
//   * ChildCreationPolicy.AdmitsCalendarEntries gated whether the create is ADVERTISED — on the document
//     resource, on the children listing the tree's New menu reads, and on GET /api/dav-collections, which the
//     Calendar tab reads. So no New anywhere, and the tab's New stayed disabled however it was ticked.
//   * TypedItemsController.CalendarFamily gated whether the server ACCEPTS the list and the create. So even a
//     caller who knew the address got 404 from both.
//
// A Schedule worked throughout, because its item mask is Booking — one of the two named — which is what made
// it look like a rights or ticking problem rather than a kind nothing had heard of.
//
// This drives the HTTP edge for all three of a bookable resource's collections, because the halves must agree:
// advertising a create the server then refuses is the affordance ADR 0543 exists to prevent, and would have
// been the result of fixing either half alone.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class CollectionEntryCreationTests
{
    private readonly E2EApiFactory _factory;

    public CollectionEntryCreationTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, Dictionary<string, Guid> Collections)> RoomCollectionsAsync()
    {
        // A USER, not a service account: GET /api/dav-collections is per-caller — it resolves colour overrides
        // and the personal default — and answers 403 to a machine principal. Asserting on that listing is the
        // point (it is the one the Calendar tab reads), so the whole test drives it as a person.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"coll-{Guid.NewGuid():N}@e2e.local";
        const string password = "collect-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Collections", canManageRepositories: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Rooms {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var masks = (await TestJson.Get(api, "/api/masks")).GetProperty("masks").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetGuid());
        var roomId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
            new { name = $"Room {Guid.NewGuid():N}", maskId = masks["Meeting room"] })).GetProperty("id").GetGuid();

        // Provisioned with the room (#1097), so nothing has to be booked first for these to exist.
        var children = (await TestJson.Get(api, $"/api/documents/{roomId}/children")).GetProperty("children")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetGuid());

        return (api, children);
    }

    // The three a bookable resource contributes. Calendar is covered by the Calendar tab's own tests; these
    // are the ones that were broken, plus Schedule as the control that always worked — without it, a failure
    // here cannot be told from the room itself being wrong.
    [Theory]
    [InlineData("Schedule")]
    [InlineData("Maintenance")]
    [InlineData("Availability")]
    public async Task A_resource_collection_advertises_its_create_and_accepts_one(string collectionName)
    {
        var (api, collections) = await RoomCollectionsAsync();
        var collectionId = Assert.Contains(collectionName, collections);

        // Half one: the create is ADVERTISED. ADR 0543 — the rel's presence is the affordance, so a client
        // that follows rels has no way in without this, which is exactly what "no context menu" was.
        var collection = await TestJson.Get(api, $"/api/documents/{collectionId}");
        Assert.Contains(collection.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "appointments");

        // ...and on the listing the Calendar tab actually reads, which is a different emitter and the one that
        // has been forgotten before (#638): a capability false here leaves New disabled however it is ticked.
        var listed = (await TestJson.Get(api, "/api/dav-collections")).GetProperty("collections").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == collectionId);
        Assert.True(listed.GetProperty("canCreateEntries").GetBoolean(),
            $"the {collectionName} collection must report canCreateEntries, or the Calendar tab's New stays dead");

        // Half two: the server ACCEPTS it. Advertising without this is worse than neither.
        // The APPOINTMENT shape (summary/start/end), not the booking shape (startsAt/endsAt): this endpoint
        // serves the calendar surface, and a booking is what the classifier makes of the .ics afterwards.
        var created = await api.PostAsJsonAsync($"/api/documents/{collectionId}/appointments", new
        {
            summary = $"{collectionName} entry",
            start = new DateTime(2027, 5, 4, 9, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2027, 5, 4, 11, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Half three: it comes BACK. The same hand-written array gated the listing, so a collection could
        // accept an entry and then show itself empty — which reads as the write having silently failed.
        var entries = (await TestJson.Get(api, $"/api/documents/{collectionId}/appointments"))
            .GetProperty("appointments").EnumerateArray().ToList();
        Assert.Single(entries);

        // The row carries where it LIVES, so "Go to" reveals it without resolving anything first (ADR 0555/0557).
        Assert.Contains(entries[0].GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "parent");

        api.Dispose();
    }
}
