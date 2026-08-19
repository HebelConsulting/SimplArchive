using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// Creating a contact and an appointment (#631) — the half the structured editors never had. They are `PUT` on
// a document that already exists, and `POST /children` makes a version-less folder-ish document, so neither
// could answer "make me a new contact"; both clients' buttons were stubs saying so.
//
// Shaped like `sections`/`notes`: a POST on the typed folder, advertised as a rel that gates the button —
// one create, one rel (ADR 0637).
//
// The rel is asserted on BOTH surfaces a client reads a folder from. That is not belt-and-braces: a rel
// emitted on the single-document GET but not on the children listing hides the action on every node a client
// builds from a listing, which is exactly what happened to `folders` and was invisible from either side.
[Collection(E2ECollection.Name)]
public class TypedItemCreateTests
{
    private readonly E2EApiFactory _factory;

    public TypedItemCreateTests(E2EApiFactory factory) => _factory = factory;

    private static bool HasRel(System.Text.Json.JsonElement resource, string rel) =>
        resource.TryGetProperty("links", out var links)
        && links.EnumerateArray().Any(l => l.GetProperty("rel").GetString() == rel);

    private static string Href(System.Text.Json.JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    /// <summary>A user, and the ids of their My Addressbook / My Calendar / My Documents.</summary>
    private async Task<(HttpClient Api, Guid Addressbook, Guid Calendar, Guid MyDocuments)> UserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"typed-{Guid.NewGuid():N}@e2e.local";
        const string password = "typed-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Typed User");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var children = (await TestJson.Get(api, $"/api/documents/{personalId}/children")).GetProperty("children").EnumerateArray().ToList();

        Guid IdOf(string name) => children.Single(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
        return (api, IdOf("My Addressbook"), IdOf("My Calendar"), IdOf("My Documents"));
    }

    [Fact]
    public async Task A_contact_is_created_from_the_collections_own_rel_and_carries_a_server_minted_UID()
    {
        var (api, addressbookId, _, _) = await UserAsync();
        using var _a = api;

        // The rel is on the RESOURCE…
        var folder = await TestJson.Get(api, $"/api/documents/{addressbookId}");
        Assert.True(HasRel(folder, "contacts"), "An Addressbook withheld the `contacts` rel, so both clients leave New Contact disabled.");
        Assert.False(HasRel(folder, "appointments"), "An Addressbook advertised `appointments`; it holds contacts.");

        var created = await TestJson.Post(api, Href(folder, "contacts"), new
        {
            formattedName = "Ada Lovelace",
            organization = "Analytical Engines",
            email = "ada@example.test",
        });

        var contactId = created.GetProperty("id").GetGuid();
        var contact = await TestJson.Get(api, $"/api/documents/{contactId}");
        Assert.Equal("Ada Lovelace", contact.GetProperty("name").GetString());

        // Classified by the finalizer, which is also what extracts the index fields — so the document really is
        // a Contact rather than a file that happens to hold a vCard.
        var card = await TestJson.Get(api, $"/api/documents/{contactId}/contact-card");
        Assert.Equal("Ada Lovelace", card.GetProperty("formattedName").GetString());
        Assert.Equal("ada@example.test", card.GetProperty("emails").EnumerateArray().Single().GetProperty("value").GetString());

        // The UID is MINTED, never a client's guess: it is the correlation key a later DAV sync matches on, and
        // a guessed one forks the contact into a duplicate on first sync (#628's lesson from the other side).
        var indexData = await TestJson.Get(api, $"/api/documents/{contactId}/index-data");
        var uid = indexData.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldName").GetString() == "Contact UID")
            .GetProperty("values").EnumerateArray().Single().GetString();
        Assert.False(string.IsNullOrWhiteSpace(uid), "The contact has no UID, so a DAV sync would duplicate it.");
        Assert.NotEqual(contactId.ToString(), uid);
    }

    [Fact]
    public async Task An_appointment_is_created_the_same_way()
    {
        var (api, _, calendarId, _) = await UserAsync();
        using var _a = api;

        var folder = await TestJson.Get(api, $"/api/documents/{calendarId}");
        Assert.True(HasRel(folder, "appointments"), "A Calendar withheld the `appointments` rel.");
        Assert.False(HasRel(folder, "contacts"), "A Calendar advertised `contacts`; it holds appointments.");

        var start = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified);
        var created = await TestJson.Post(api, Href(folder, "appointments"), new
        {
            summary = "Quarterly review",
            start,
            end = start.AddHours(1),
            location = "Room 3",
        });

        var appointmentId = created.GetProperty("id").GetGuid();
        var appointment = await TestJson.Get(api, $"/api/documents/{appointmentId}/appointment");
        Assert.Equal("Quarterly review", appointment.GetProperty("summary").GetString());
        Assert.Equal("Room 3", appointment.GetProperty("location").GetString());
    }

    [Fact]
    public async Task The_rels_are_on_the_CHILDREN_listing_too_where_both_clients_build_their_nodes()
    {
        // The failure this rules out is the one `folders` actually had: emitted on the resource and not on the
        // listing, so the affordance vanished on every node a client built from a listing — invisible from
        // either side, and the reason this assertion exists at all (ADR 0637).
        var (api, _, _, _) = await UserAsync();
        using var _a = api;

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var rows = (await TestJson.Get(api, $"/api/documents/{personalId}/children")).GetProperty("children").EnumerateArray().ToList();

        Assert.True(HasRel(rows.Single(r => r.GetProperty("name").GetString() == "My Addressbook"), "contacts"),
            "The children listing omitted `contacts` on My Addressbook, so New Contact is hidden on the node both clients actually hold.");
        Assert.True(HasRel(rows.Single(r => r.GetProperty("name").GetString() == "My Calendar"), "appointments"),
            "The children listing omitted `appointments` on My Calendar.");

        // …and an ordinary folder advertises neither, which is what tells a client to leave both off its menu.
        var myDocuments = rows.Single(r => r.GetProperty("name").GetString() == "My Documents");
        Assert.False(HasRel(myDocuments, "contacts"));
        Assert.False(HasRel(myDocuments, "appointments"));
    }

    [Fact]
    public async Task Creating_one_in_the_wrong_collection_is_not_found_rather_than_refused()
    {
        // These sub-resources do not EXIST on an ordinary folder, which is the same thing the absent rel says.
        // A 403 would imply the caller might be granted them — the same answer the notebook creates give.
        var (api, addressbookId, calendarId, myDocumentsId) = await UserAsync();
        using var _a = api;

        Assert.Equal(HttpStatusCode.NotFound,
            (await api.PostAsJsonAsync($"/api/documents/{myDocumentsId}/contacts", new { formattedName = "Nobody" })).StatusCode);

        // …and crossed over: a contact into a calendar, an appointment into an addressbook.
        Assert.Equal(HttpStatusCode.NotFound,
            (await api.PostAsJsonAsync($"/api/documents/{calendarId}/contacts", new { formattedName = "Nobody" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await api.PostAsJsonAsync($"/api/documents/{addressbookId}/appointments", new { summary = "Nothing" })).StatusCode);
    }

    [Fact]
    public async Task Two_contacts_with_the_same_name_both_land()
    {
        // The DbContext refuses a sibling-name clash, and a create should not: a person may know two Ada
        // Lovelaces, and the second must not fail with an error about the first.
        var (api, addressbookId, _, _) = await UserAsync();
        using var _a = api;

        var href = $"/api/documents/{addressbookId}/contacts";
        var first = await TestJson.Post(api, href, new { formattedName = "Ada Lovelace" });
        var second = await TestJson.Post(api, href, new { formattedName = "Ada Lovelace" });

        Assert.NotEqual(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal("Ada Lovelace (2)",
            (await TestJson.Get(api, $"/api/documents/{second.GetProperty("id").GetGuid()}")).GetProperty("name").GetString());
    }
}
