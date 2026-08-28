namespace SimplArchive.EndToEndTests;

// Listing a typed collection's entries WITH their index fields (#660).
//
// Both tabs used to build every row from the children listing, which carries a name and nothing else — so When,
// Where, e-mail and phone were rendered from hardcoded empty strings, and the detail pane beside them was blank
// by construction. It also made a month view impossible: with no Start there is nothing to place in a day cell.
//
// So the typed rel now serves both methods on one address: GET lists, POST creates. The rel is therefore
// advertised to anyone who can SEE the collection — withholding it from a reader would leave them a calendar
// with no appointments in it — and the right to CREATE rides as the collection's `canCreateEntries` capability,
// because one rel cannot say "read yes, write no".
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TypedItemListTests
{
    private readonly E2EApiFactory _factory;

    public TypedItemListTests(E2EApiFactory factory) => _factory = factory;

    private static string Href(System.Text.Json.JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    private async Task<(HttpClient Api, Guid Addressbook, Guid Calendar)> UserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"typedlist-{Guid.NewGuid():N}@e2e.local";
        const string password = "typedlist-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Typed List");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var children = (await TestJson.Get(api, $"/api/documents/{personalId}/children")).GetProperty("children").EnumerateArray().ToList();

        Guid IdOf(string name) => children.Single(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
        return (api, IdOf("My Addressbook"), IdOf("My Calendar"));
    }

    [Fact]
    public async Task An_appointment_row_carries_the_time_and_place_a_list_has_to_show()
    {
        var (api, _, calendarId) = await UserAsync();
        using var _a = api;

        var createHref = Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments");
        var start = new DateTime(2026, 9, 1, 17, 0, 0, DateTimeKind.Unspecified);
        await TestJson.Post(api, createHref, new
        {
            summary = "Shalin Liu — early",
            start,
            end = start.AddHours(2),
            location = "Rockport, MA",
        });

        // The SAME rel, followed with GET rather than POST.
        var listed = (await TestJson.Get(api, createHref)).GetProperty("appointments").EnumerateArray().ToList();
        var row = Assert.Single(listed);

        Assert.Equal("Shalin Liu — early", row.GetProperty("name").GetString());
        Assert.Equal("Rockport, MA", row.GetProperty("location").GetString());
        Assert.False(row.GetProperty("allDay").GetBoolean());

        // The whole point of #660: the hour survives to the row, so a list can show a time and order by it.
        var startText = row.GetProperty("start").GetString();
        Assert.NotNull(startText);
        Assert.Contains("17:00", startText!, StringComparison.Ordinal);

        // And the row carries its own address, so acting on it never reads the pane's loaded state (ADR 0559).
        Assert.Equal($"/api/documents/{row.GetProperty("id").GetGuid()}", Href(row, "self"));
    }

    [Fact]
    public async Task An_all_day_row_says_so_rather_than_inventing_midnight()
    {
        var (api, _, calendarId) = await UserAsync();
        using var _a = api;

        var createHref = Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments");
        var day = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Unspecified);
        await TestJson.Post(api, createHref, new { summary = "Lluís Coloma Trio", start = day, end = day, isAllDay = true });

        var row = Assert.Single((await TestJson.Get(api, createHref)).GetProperty("appointments").EnumerateArray().ToList());

        // A day is not a moment. The flag is what lets a client format it as one without guessing from the text.
        Assert.True(row.GetProperty("allDay").GetBoolean());
        Assert.Equal("2026-08-21", row.GetProperty("start").GetString());
    }

    [Fact]
    public async Task A_contact_row_carries_the_fields_the_detail_pane_shows()
    {
        var (api, addressbookId, _) = await UserAsync();
        using var _a = api;

        var createHref = Href(await TestJson.Get(api, $"/api/documents/{addressbookId}"), "contacts");
        await TestJson.Post(api, createHref, new
        {
            formattedName = "Silvan Zingg",
            organization = "Boogie Woogie",
            emails = new[] { new { value = "silvan@example.invalid", type = "work" } },
            phones = new[] { new { value = "+41 91 000 00 00", type = "work" } },
        });

        var row = Assert.Single((await TestJson.Get(api, createHref)).GetProperty("contacts").EnumerateArray().ToList());

        Assert.Equal("Silvan Zingg", row.GetProperty("name").GetString());
        Assert.Equal("Boogie Woogie", row.GetProperty("organization").GetString());

        // Read BY NAME on the server: a document has one row per index field, so a lookup filtered only by
        // document id returns an arbitrary one — which is how a vCard's UID once became its phone number (#628).
        Assert.Equal("silvan@example.invalid", row.GetProperty("email").GetString());
        Assert.Equal("+41 91 000 00 00", row.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task The_rel_is_the_collections_and_a_wrongly_typed_folder_does_not_have_it()
    {
        var (api, addressbookId, calendarId) = await UserAsync();
        using var _a = api;

        // Listing a calendar through the addressbook's route is not a refusal but a 404: the sub-resource does
        // not EXIST on that folder, which is what its absent rel already says. A 403 would imply it might be
        // granted.
        var wrong = await api.GetAsync($"/api/documents/{addressbookId}/appointments");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wrong.StatusCode);

        var alsoWrong = await api.GetAsync($"/api/documents/{calendarId}/contacts");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, alsoWrong.StatusCode);

        // Every GET has its companion HEAD (standing convention) — same status, no body.
        var head = await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{calendarId}/appointments"));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, head.StatusCode);
    }

    [Fact]
    public async Task The_collection_listing_says_whether_this_caller_may_add_an_entry()
    {
        var (api, _, calendarId) = await UserAsync();
        using var _a = api;

        var collections = (await TestJson.Get(api, "/api/dav-collections?kind=calendar"))
            .GetProperty("collections").EnumerateArray().ToList();
        var mine = collections.Single(c => c.GetProperty("id").GetGuid() == calendarId);

        // The owner may create — and the flag, not the rel, is what New is gated on now that the rel serves the
        // listing too. Gating the button on the rel would light it up for a reader and fail on click.
        Assert.True(mine.GetProperty("canCreateEntries").GetBoolean());

        // The rel is still there, because it is how the tab READS the collection.
        Assert.Equal($"/api/documents/{calendarId}/appointments", Href(mine, "appointments"));
    }
}
