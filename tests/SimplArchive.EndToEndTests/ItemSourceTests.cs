using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// The raw source behind a structured item (#648, ADR 0643) — the escape hatch for everything the form does not
// model. A save here REPLACES rather than merges, which is what "raw" has to mean and is also what removes the
// safety net the merge provided, so the two refusals below are load-bearing rather than defensive.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ItemSourceTests
{
    private readonly E2EApiFactory _factory;

    public ItemSourceTests(E2EApiFactory factory) => _factory = factory;

    private static string Href(System.Text.Json.JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    /// <summary>A user with a contact carrying a property the structured form does not model.</summary>
    private async Task<(HttpClient Api, Guid ContactId, Guid CalendarId)> UserWithContactAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"source-{Guid.NewGuid():N}@e2e.local";
        const string password = "source-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Source User");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var children = (await TestJson.Get(api, $"/api/documents/{personalId}/children")).GetProperty("children").EnumerateArray().ToList();
        Guid IdOf(string name) => children.Single(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

        var addressbook = await TestJson.Get(api, $"/api/documents/{IdOf("My Addressbook")}");
        var contactId = (await TestJson.Post(api, Href(addressbook, "contacts"), new { formattedName = "Vera Volmet" }))
            .GetProperty("id").GetGuid();

        return (api, contactId, IdOf("My Calendar"));
    }

    private static async Task<(string Text, string ETag)> ReadSourceAsync(HttpClient api, string href)
    {
        using var response = await api.GetAsync(href);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return (body.GetProperty("text").GetString()!, response.Headers.ETag!.Tag!);
    }

    private static async Task<HttpResponseMessage> SaveSourceAsync(HttpClient api, string href, string text, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, href)
        {
            Content = JsonContent.Create(new { text }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await api.SendAsync(request);
    }

    [Fact]
    public async Task The_raw_card_is_reached_from_the_structured_one_and_a_save_REPLACES_it()
    {
        var (api, contactId, _) = await UserWithContactAsync();
        using var _a = api;

        // Reached by following the rel off the structured resource the client already holds — one read, many
        // follows (ADR 0557), rather than a second address the client has to know about.
        var card = await TestJson.Get(api, $"/api/documents/{contactId}/contact-card");
        var sourceHref = Href(card, "source");

        var (text, etag) = await ReadSourceAsync(api, sourceHref);
        Assert.StartsWith("BEGIN:VCARD", text, StringComparison.Ordinal);
        Assert.Contains("Vera Volmet", text, StringComparison.Ordinal);

        // A property no structured field models. Adding it through the raw editor is the entire point of the
        // feature: there is nowhere else in the product it can be typed.
        var uid = text.Split("\r\n").Single(l => l.StartsWith("UID:", StringComparison.Ordinal));
        var replacement = $"BEGIN:VCARD\r\nVERSION:3.0\r\n{uid}\r\nFN:Vera Volmet\r\nORG:VOLMET Geneva\r\nX-ABShowAs:COMPANY\r\nEND:VCARD\r\n";
        (await SaveSourceAsync(api, sourceHref, replacement, etag)).EnsureSuccessStatusCode();

        var (saved, _) = await ReadSourceAsync(api, sourceHref);
        Assert.Contains("X-ABShowAs:COMPANY", saved, StringComparison.Ordinal);

        // REPLACED, not merged: the structured editor reads the new card, and the organisation the raw save
        // introduced is what it shows. A merge would have been indistinguishable here — which is why the
        // deletion below is the assertion that actually proves it.
        var afterCard = await TestJson.Get(api, $"/api/documents/{contactId}/contact-card");
        Assert.Equal("VOLMET Geneva", afterCard.GetProperty("organization").GetString());
    }

    [Fact]
    public async Task Deleting_a_line_in_the_raw_card_deletes_the_property()
    {
        // The half that separates replace from merge. If the source save went through the composer's merge, the
        // organisation would come back from the previous version and the editor would silently undo the user's
        // edit while reporting success.
        var (api, contactId, _) = await UserWithContactAsync();
        using var _a = api;

        var sourceHref = Href(await TestJson.Get(api, $"/api/documents/{contactId}/contact-card"), "source");
        var (text, etag) = await ReadSourceAsync(api, sourceHref);
        var uid = text.Split("\r\n").Single(l => l.StartsWith("UID:", StringComparison.Ordinal));

        var withOrg = $"BEGIN:VCARD\r\nVERSION:3.0\r\n{uid}\r\nFN:Vera Volmet\r\nORG:Contoso\r\nEND:VCARD\r\n";
        (await SaveSourceAsync(api, sourceHref, withOrg, etag)).EnsureSuccessStatusCode();
        Assert.Equal("Contoso",
            (await TestJson.Get(api, $"/api/documents/{contactId}/contact-card")).GetProperty("organization").GetString());

        var (_, etag2) = await ReadSourceAsync(api, sourceHref);
        var withoutOrg = $"BEGIN:VCARD\r\nVERSION:3.0\r\n{uid}\r\nFN:Vera Volmet\r\nEND:VCARD\r\n";
        (await SaveSourceAsync(api, sourceHref, withoutOrg, etag2)).EnsureSuccessStatusCode();

        var after = await TestJson.Get(api, $"/api/documents/{contactId}/contact-card");
        Assert.True(
            after.GetProperty("organization").ValueKind == System.Text.Json.JsonValueKind.Null
            || string.IsNullOrEmpty(after.GetProperty("organization").GetString()),
            "The organisation came back after being deleted in the raw card, so the save merged instead of replacing.");
    }

    [Fact]
    public async Task A_raw_save_that_changes_the_UID_is_refused_and_writes_nothing()
    {
        // Changing the UID does not rename the item, it makes it a DIFFERENT one — so the next sync keeps the
        // old copy on the phone and adds the new. Refused rather than silently rewritten, because a raw editor
        // exists so the user can mean what they wrote.
        var (api, contactId, _) = await UserWithContactAsync();
        using var _a = api;

        var sourceHref = Href(await TestJson.Get(api, $"/api/documents/{contactId}/contact-card"), "source");
        var (text, etag) = await ReadSourceAsync(api, sourceHref);

        var forked = $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{Guid.NewGuid()}\r\nFN:Vera Volmet\r\nEND:VCARD\r\n";
        using var refused = await SaveSourceAsync(api, sourceHref, forked, etag);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("ITEM_SOURCE_UID_CHANGED", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Nothing was written — the stored card is byte-for-byte what it was.
        var (afterRefusal, etag2) = await ReadSourceAsync(api, sourceHref);
        Assert.Equal(text, afterRefusal);

        // …while REMOVING the UID line is not a change and is accepted: the user deleting a line they never
        // wrote is not the same act as asserting a different identity, so the stored UID is kept.
        var withoutUid = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Vera Volmet\r\nEND:VCARD\r\n";
        (await SaveSourceAsync(api, sourceHref, withoutUid, etag2)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Text_that_is_not_the_format_is_refused_before_anything_is_written()
    {
        var (api, contactId, _) = await UserWithContactAsync();
        using var _a = api;

        var sourceHref = Href(await TestJson.Get(api, $"/api/documents/{contactId}/contact-card"), "source");
        var (text, etag) = await ReadSourceAsync(api, sourceHref);

        using var refused = await SaveSourceAsync(api, sourceHref, "just some words I typed", etag);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("UNPARSABLE_ITEM_SOURCE", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var (unchanged, _) = await ReadSourceAsync(api, sourceHref);
        Assert.Equal(text, unchanged);
    }

    [Fact]
    public async Task A_save_needs_If_Match_and_a_stale_token_loses()
    {
        // The token is the DOCUMENT's and is shared with the structured editor, so editing the same item through
        // the form and through the raw box collide with each other rather than each quietly winning.
        var (api, contactId, _) = await UserWithContactAsync();
        using var _a = api;

        var sourceHref = Href(await TestJson.Get(api, $"/api/documents/{contactId}/contact-card"), "source");
        var (text, etag) = await ReadSourceAsync(api, sourceHref);

        using var noHeader = await api.PutAsJsonAsync(sourceHref, new { text });
        Assert.Equal(HttpStatusCode.PreconditionRequired, noHeader.StatusCode);

        // A structured save moves the token; the raw save still holding the old one must lose.
        var cardHref = $"/api/documents/{contactId}/contact-card";
        using var structuredSave = new HttpRequestMessage(HttpMethod.Put, cardHref)
        {
            Content = JsonContent.Create(new { formattedName = "Vera Volmet", organization = "Elsewhere" }),
        };
        structuredSave.Headers.TryAddWithoutValidation("If-Match", etag);
        (await api.SendAsync(structuredSave)).EnsureSuccessStatusCode();

        using var stale = await SaveSourceAsync(api, sourceHref, text, etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task The_appointment_side_answers_the_same_way()
    {
        // One implementation serves both families, so this asserts the wiring rather than repeating the rules:
        // the rel is advertised, the source is iCalendar, and a replacement round-trips.
        var (api, _, calendarId) = await UserWithContactAsync();
        using var _a = api;

        var calendar = await TestJson.Get(api, $"/api/documents/{calendarId}");
        var start = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified);
        var appointmentId = (await TestJson.Post(api, Href(calendar, "appointments"), new
        {
            summary = "Weekly sync",
            start,
            end = start.AddHours(1),
        })).GetProperty("id").GetGuid();

        var entry = await TestJson.Get(api, $"/api/documents/{appointmentId}/appointment");
        var sourceHref = Href(entry, "source");

        var (text, etag) = await ReadSourceAsync(api, sourceHref);
        Assert.StartsWith("BEGIN:VCALENDAR", text, StringComparison.Ordinal);

        // A VALARM: the appointment equivalent of the unmodelled property, and the reason the escape hatch is
        // not contacts-only — nothing in the structured form can add one.
        var withAlarm = text.Replace(
            "END:VEVENT",
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nDESCRIPTION:Weekly sync\r\nEND:VALARM\r\nEND:VEVENT",
            StringComparison.Ordinal);
        (await SaveSourceAsync(api, sourceHref, withAlarm, etag)).EnsureSuccessStatusCode();

        var (saved, _) = await ReadSourceAsync(api, sourceHref);
        Assert.Contains("TRIGGER:-PT15M", saved, StringComparison.Ordinal);

        // A vCard offered to the calendar's source is refused: the family is decided by the route, and this is
        // the check that keeps one implementation from serving the wrong one.
        var (_, etag2) = await ReadSourceAsync(api, sourceHref);
        using var wrongFamily = await SaveSourceAsync(
            api, sourceHref, "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Nope\r\nEND:VCARD\r\n", etag2);
        Assert.Equal(HttpStatusCode.BadRequest, wrongFamily.StatusCode);
    }

    [Fact]
    public async Task A_document_that_is_not_a_card_has_no_source_at_all()
    {
        // The rel is on the structured resource, so a plain document never advertises one — and asking anyway
        // is a 404 rather than a refusal, because the sub-resource does not EXIST there (ADR 0543).
        var (api, _, _) = await UserWithContactAsync();
        using var _a = api;

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var myDocumentsId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents").GetProperty("id").GetGuid();

        using var response = await api.GetAsync($"/api/documents/{myDocumentsId}/contact-card/source");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
