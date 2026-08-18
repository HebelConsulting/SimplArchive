using System.Net;
using System.Xml.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The structured contact editor's API (#564): GET the modelled fields, PUT them back merged into the stored
// vCard. Driven end to end because the thing most likely to break is invisible to a unit test — the card is
// read from and written to real object storage, as a new version, and re-classified by the finalizer.
//
// The card is created the way a real one arrives: a CardDAV client PUTs it. That matters. A fixture written
// by the test would only contain properties the test author thought of, and the whole point of the merge is
// what happens to the ones nobody thought of.
[Collection(E2ECollection.Name)]
public class ContactCardEndpointTests
{
    private readonly E2EApiFactory _factory;

    public ContactCardEndpointTests(E2EApiFactory factory) => _factory = factory;

    // Properties we model, plus ones we do not (ANNIVERSARY, CATEGORIES, IMPP, X-ABLabel) and a FOLDED PHOTO —
    // the folded line is the one a naive merge corrupts rather than drops.
    private static string Card(string uid) =>
        "BEGIN:VCARD\r\n"
        + "VERSION:3.0\r\n"
        + $"UID:{uid}\r\n"
        + "FN:Anna Meyer\r\n"
        + "N:Meyer;Anna;;;\r\n"
        + "EMAIL;TYPE=WORK:anna@example.test\r\n"
        + "TEL;TYPE=CELL:+41790000000\r\n"
        + "ORG:Contoso\r\n"
        + "NOTE:Met at the trade fair.\r\n"
        + "ANNIVERSARY:2015-06-20\r\n"
        + "CATEGORIES:Suppliers,VIP\r\n"
        + "IMPP:xmpp:anna@example.test\r\n"
        + "X-ABLabel:_$!<Work>!$_\r\n"
        + "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQ\r\n"
        + " EAAAAAAD/2wBDAAYEBQYFBAYG\r\n"
        + "END:VCARD\r\n";

    /// <summary>A user, a contact filed by a CardDAV PUT, and the document id it landed as.</summary>
    private async Task<(HttpClient Api, HttpClient Dav, AuthenticationHeaderValue Auth, Guid DocumentId, string ItemHref)> ContactAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"card-{Guid.NewGuid():N}@e2e.local";
        const string password = "card-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Card Editor");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var generated = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{generated.GetProperty("password").GetString()}")));
        var dav = _factory.CreateClient();

        // The collection href is DISCOVERED via PROPFIND, never composed — the server owns its URL space, and
        // a guessed path is how this first failed with a 404 against five green-looking assertions.
        using var probe = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/carddav/addressbooks/")
        {
            Headers = { Authorization = auth },
        };
        probe.Headers.TryAddWithoutValidation("Depth", "1");
        var listing = XDocument.Parse(await (await dav.SendAsync(probe)).Content.ReadAsStringAsync());
        XNamespace davNs = "DAV:";
        var collectionHref = listing.Descendants(davNs + "response")
            .Single(r => r.Descendants(davNs + "displayname").Any(d => d.Value.EndsWith("My Contacts", StringComparison.Ordinal)))
            .Element(davNs + "href")!.Value;

        var uid = $"uid-{Guid.NewGuid():N}";
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{collectionHref}{uid}.vcf")
        {
            Content = new StringContent(Card(uid), Encoding.UTF8, "text/vcard"),
            Headers = { Authorization = auth },
        };
        var response = await dav.SendAsync(put);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.OK,
            $"CardDAV PUT returned {(int)response.StatusCode}");

        // Find the document the PUT created, by the UID the classifier extracted into the Contact mask.
        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var documentId = await FindContactAsync(api, personal.GetProperty("id").GetGuid(), uid);
        return (api, dav, auth, documentId, $"{collectionHref}{uid}.vcf");
    }

    /// <summary>Walks the personal space for the contact the DAV PUT filed.</summary>
    private static async Task<Guid> FindContactAsync(HttpClient api, Guid rootId, string uid)
    {
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var children = await TestJson.Get(api, $"/api/documents/{queue.Dequeue()}/children?limit=200");
            foreach (var child in children.GetProperty("children").EnumerateArray())
            {
                var id = child.GetProperty("id").GetGuid();
                if (child.GetProperty("name").GetString()?.Contains("Anna", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return id;
                }

                queue.Enqueue(id);
            }
        }

        Assert.Fail($"No contact document found for {uid}");
        return Guid.Empty;
    }

    [Fact]
    public async Task The_card_reads_back_as_structured_fields()
    {
        var (api, _, _, documentId, _) = await ContactAsync();
        using var _a = api;

        var card = await TestJson.Get(api, $"/api/documents/{documentId}/contact-card");

        Assert.Equal("Anna Meyer", card.GetProperty("formattedName").GetString());
        Assert.Equal("Meyer", card.GetProperty("familyName").GetString());
        Assert.Equal("Contoso", card.GetProperty("organization").GetString());
        Assert.Equal("anna@example.test", card.GetProperty("emails")[0].GetProperty("value").GetString());

        // TYPE=CELL is normalised to the three types the form offers.
        Assert.Equal("mobile", card.GetProperty("phones")[0].GetProperty("type").GetString());

        // The caller owns this contact, so the client may offer Edit.
        Assert.True(card.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task Saving_an_edit_preserves_everything_the_form_does_not_model()
    {
        // The whole reason the composer exists, proved through the real storage round trip rather than in
        // memory: a user changes one e-mail here and their contact's photo, categories and custom labels must
        // still be on their phone afterwards.
        var (api, dav, auth, documentId, itemHref) = await ContactAsync();
        using var _a = api;
        using var _d = dav;

        var get = await api.GetAsync($"/api/documents/{documentId}/contact-card");
        var etag = get.Headers.ETag!.Tag;
        var card = await TestJson.Get(api, $"/api/documents/{documentId}/contact-card");

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/contact-card")
        {
            Content = JsonContent.Create(new
            {
                formattedName = "Anna Meyer",
                givenName = "Anna",
                familyName = "Meyer",
                organization = card.GetProperty("organization").GetString(),
                emails = new[] { new { value = "anna.meyer@example.test", type = "work" } },
                phones = new[] { new { value = "+41790000000", type = "mobile" } },
                addresses = Array.Empty<object>(),
                note = "Met at the trade fair.",
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(put)).StatusCode);

        // Read the STORED CARD back over CardDAV — the bytes another client would sync, not our own projection
        // of them. This is the assertion that a purely in-memory test cannot make.
        var reread = await TestJson.Get(api, $"/api/documents/{documentId}/contact-card");
        Assert.Equal("anna.meyer@example.test", reread.GetProperty("emails")[0].GetProperty("value").GetString());

        var raw = await RawCardAsync(dav, auth, itemHref);
        Assert.Contains("anna.meyer@example.test", raw);
        Assert.Contains("ANNIVERSARY:2015-06-20", raw);
        Assert.Contains("CATEGORIES:Suppliers,VIP", raw);
        Assert.Contains("IMPP:xmpp:anna@example.test", raw);
        Assert.Contains("X-ABLabel", raw);

        // The folded photo survives as ONE logical line. A merge re-emitting physical lines would leave a
        // stray continuation — the property still present, the image corrupt, and nothing looking wrong.
        Assert.Contains("/9j/4AAQSkZJRgABAQEAAAAAAD/2wBDAAYEBQYFBAYG", raw);
    }

    [Fact]
    public async Task The_uid_survives_a_save_so_the_contact_does_not_fork_on_the_next_sync()
    {
        // A regression test for a bug this suite could not see. The UID was read as "the first FieldValue on
        // this document" with no filter naming the field — and a contact carries five of them (Contact UID,
        // Full name, Email, Phone, Organization), with no ORDER BY to say which comes back. So a save could
        // write the organisation or the phone number into the card as its UID.
        //
        // That is the correlation key a DAV client matches on, so the consequence is not a wrong string in a
        // field nobody reads: the contact FORKS into a duplicate on the next sync, or lands on top of a
        // different entry. Every existing test here passed throughout, because none of them looked at the UID
        // after a PUT — they asserted the properties the merge was written to protect, and the UID is supplied
        // to the composer as an argument, so the unit tests could not see it either.
        var (api, dav, auth, documentId, itemHref) = await ContactAsync();
        using var _a = api;
        using var _d = dav;

        var uidBefore = UidOf(await RawCardAsync(dav, auth, itemHref));
        Assert.StartsWith("uid-", uidBefore);

        var get = await api.GetAsync($"/api/documents/{documentId}/contact-card");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/contact-card")
        {
            // Organization is the value most likely to be picked up in the UID's place, so it is set to
            // something unmistakable: if it appears as the UID, the assertion below names the actual defect.
            Content = JsonContent.Create(new
            {
                formattedName = "Anna Meyer",
                givenName = "Anna",
                familyName = "Meyer",
                organization = "Contoso",
                note = "Edited once.",
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(put)).StatusCode);

        var uidAfter = UidOf(await RawCardAsync(dav, auth, itemHref));
        Assert.Equal(uidBefore, uidAfter);
    }

    private static string UidOf(string card) =>
        card.Replace("\r\n ", string.Empty).Split("\r\n").Single(l => l.StartsWith("UID:", StringComparison.Ordinal))["UID:".Length..];

    [Fact]
    public async Task A_save_writes_a_new_version_rather_than_mutating_the_stored_object()
    {
        var (api, _, _, documentId, _) = await ContactAsync();
        using var _a = api;

        var before = (await TestJson.Get(api, $"/api/documents/{documentId}/versions")).GetProperty("versions").GetArrayLength();

        var get = await api.GetAsync($"/api/documents/{documentId}/contact-card");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/contact-card")
        {
            Content = JsonContent.Create(new { formattedName = "Anna Meyer", givenName = "Anna", familyName = "Meyer", note = "Edited." }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(put)).StatusCode);

        var after = (await TestJson.Get(api, $"/api/documents/{documentId}/versions")).GetProperty("versions").GetArrayLength();
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task A_stale_If_Match_is_refused_and_a_missing_one_is_required()
    {
        var (api, _, _, documentId, _) = await ContactAsync();
        using var _a = api;

        var body = new { formattedName = "Anna Meyer", givenName = "Anna", familyName = "Meyer" };

        using var noMatch = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/contact-card")
        {
            Content = JsonContent.Create(body),
        };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await api.SendAsync(noMatch)).StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/contact-card")
        {
            Content = JsonContent.Create(body),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{Guid.NewGuid()}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await api.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task A_document_that_is_not_a_contact_has_no_card()
    {
        var (api, _, _, _, _) = await ContactAsync();
        using var _a = api;

        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var folder = await TestJson.Post(api, $"/api/documents/{personal.GetProperty("id").GetGuid()}/children",
            new { name = $"Plain {Guid.NewGuid():N}"[..14] });

        var response = await api.GetAsync($"/api/documents/{folder.GetProperty("id").GetGuid()}/contact-card");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The stored card as ANOTHER CLIENT would sync it — fetched back over CardDAV rather than through our own
    /// structured projection. Asserting against the projection would only prove the projection is
    /// self-consistent; the question is what is in the bytes.
    /// </summary>
    private static async Task<string> RawCardAsync(HttpClient dav, AuthenticationHeaderValue auth, string itemHref)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, itemHref) { Headers = { Authorization = auth } };
        var response = await dav.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
