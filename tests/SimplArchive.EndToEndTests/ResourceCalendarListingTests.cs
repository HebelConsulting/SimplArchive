using System.Text.Json;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// A bookable resource's three calendars in the app's own listing (ADRs 0744/0778/0780): the Schedule that
// says when it is spoken for, the Maintenance collection that says when it is unavailable, and the
// Availability collection that says when it is offered.
//
// All three are calendars on the wire and all three belong in the Calendar tab. Only the Schedule got there:
// the listing named its masks one by one, so Maintenance and Availability — added to DavCollectionKinds.All
// when their slices landed — never reached it. The collections existed, CalDAV served them, and BOTH clients
// showed nothing. The same trap had already fired once in DavProtocol, which carries a comment saying so.
//
// The test is therefore written against the KIND TABLE rather than against three literal names: a new kind
// added to the table must appear here without anybody remembering to edit this file, which is the property
// the hand-written list lacked.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ResourceCalendarListingTests
{
    private readonly E2EApiFactory _factory;

    public ResourceCalendarListingTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Every_ics_collection_kind_lists_as_a_calendar_and_is_tellable_apart()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"sched-{Guid.NewGuid():N}@e2e.local";
        const string password = "modadmin-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Scheduler", isTenantAdmin: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Sched {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{userId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });

        var masks = (await TestJson.Get(owner, "/api/masks")).GetProperty("masks").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetGuid());
        var roomName = $"Room {Guid.NewGuid():N}"[..14];
        var roomId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = roomName, maskId = masks["Meeting room"] })).GetProperty("id").GetGuid();

        // NOT created here any more (#1097): assigning a bookable mask provisions all three, so a calendar
        // client subscribing to a freshly created room finds something to PUT into without anybody booking
        // in the app first — which is what ADR 0744 claimed and what was not true.
        var wanted = new[] { "Schedule", "Maintenance", "Availability" };

        var listed = (await TestJson.Get(api, "/api/dav-collections?kind=calendar"))
            .GetProperty("collections").EnumerateArray()
            .Where(c => c.GetProperty("displayName").GetString()!.Contains(roomName, StringComparison.Ordinal))
            .ToList();

        // Every .ics kind the table knows, not three hard-coded names.
        var expected = SimplArchive.Domain.CalDav.DavCollectionKinds.All
            .Where(k => k.Extension == ".ics" && k.FolderMaskId != WellKnownMaskIds.Calendar)
            .Count();
        Assert.Equal(expected, listed.Count);

        foreach (var name in wanted)
        {
            Assert.Contains(listed, c => c.GetProperty("name").GetString() == name);
        }

        // And tellable apart once overlaid. Three calendars drawn in one default colour is an overlay
        // nobody can read, which is the same as not having one.
        var colours = listed.Select(c => c.GetProperty("color").GetString()).ToList();
        Assert.DoesNotContain(colours, c => string.IsNullOrEmpty(c));
        Assert.Equal(colours.Count, colours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
