using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SimplArchive.EndToEndTests;

// CalDAV + CardDAV writes (#564 slice 2, ADR 0620), driven over raw HTTP the way a sync client does: PUT an
// item, PUT it again as a new VERSION of the same document, collide on a stale If-Match, DELETE into the
// recycle bin. A [Theory] over both protocols, since one implementation serves both.
[Collection(E2ECollection.Name)]
public class CalDavWriteTests
{
    private static readonly XNamespace Dav = "DAV:";

    private readonly E2EApiFactory _factory;

    public CalDavWriteTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Protocol(string Base, string Collections, string Extension, string DefaultFolder);

    private static readonly Protocol CalDav = new("/caldav", "calendars", ".ics", "My Calendar");
    private static readonly Protocol CardDav = new("/carddav", "addressbooks", ".vcf", "My Contacts");

    public static TheoryData<string> Protocols => ["caldav", "carddav"];

    private static Protocol Of(string name) => name == "caldav" ? CalDav : CardDav;

    private static string Item(Protocol protocol, string uid, string title) =>
        protocol == CalDav
            ? $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SimplArchive//E2E//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\n"
              + $"DTSTAMP:20260817T090000Z\r\nDTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\n"
              + $"SUMMARY:{title}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
            : $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:{title}\r\nN:{title};;;;\r\nEND:VCARD\r\n";

    private async Task<(HttpClient Client, AuthenticationHeaderValue Auth, HttpClient Api)> SeedAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"davw-{Guid.NewGuid():N}@e2e.local";
        const string password = "davw-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav Writer");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });
        var generated = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{generated.GetProperty("password").GetString()}")));
        return (_factory.CreateClient(), auth, api);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, AuthenticationHeaderValue auth, string method, string url, string? body = null, string? ifMatch = null, string? ifNoneMatch = null, string depth = "1")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Headers = { Authorization = auth } };
        request.Headers.TryAddWithoutValidation("Depth", depth);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> CollectionHrefAsync(HttpClient client, AuthenticationHeaderValue auth, Protocol protocol)
    {
        var response = await SendAsync(client, auth, "PROPFIND", $"{protocol.Base}/{protocol.Collections}/");
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.Descendants(Dav + "response")
            .Single(r => r.Descendants(Dav + "displayname").Any(d => d.Value.EndsWith(protocol.DefaultFolder, StringComparison.Ordinal)))
            .Element(Dav + "href")!.Value;
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Put_creates_then_versions_the_same_document_and_delete_soft_deletes_it(string protocolName)
    {
        var protocol = Of(protocolName);
        var (client, auth, api) = await SeedAsync();
        using var _1 = client;
        using var _2 = api;

        var collectionHref = await CollectionHrefAsync(client, auth, protocol);
        var uid = $"w-{Guid.NewGuid():N}";
        var itemHref = $"{collectionHref}{uid}{protocol.Extension}";

        // ---- Create ----------------------------------------------------------------------------------------
        var created = await SendAsync(client, auth, "PUT", itemHref, Item(protocol, uid, "First title"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var firstETag = created.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(firstETag));

        // It reads back through the protocol, classified (the containment invariant would have refused it
        // otherwise) and named after its own title rather than the resource name.
        var fetched = await SendAsync(client, auth, "GET", itemHref);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Contains("First title", await fetched.Content.ReadAsStringAsync());

        // ---- Replace → a NEW VERSION of the SAME document ---------------------------------------------------
        var replaced = await SendAsync(client, auth, "PUT", itemHref, Item(protocol, uid, "Second title"));
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);

        var listing = await SendAsync(client, auth, "PROPFIND", collectionHref);
        var hrefs = XDocument.Parse(await listing.Content.ReadAsStringAsync())
            .Descendants(Dav + "href").Select(e => e.Value)
            .Where(h => h.Contains(uid, StringComparison.Ordinal)).ToList();
        Assert.Single(hrefs); // one document, two versions — not two documents

        Assert.Contains("Second title", await (await SendAsync(client, auth, "GET", itemHref)).Content.ReadAsStringAsync());

        // ---- A stale If-Match is a 412, and changes nothing --------------------------------------------------
        var stale = await SendAsync(client, auth, "PUT", itemHref, Item(protocol, uid, "Third title"), ifMatch: firstETag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Contains("Second title", await (await SendAsync(client, auth, "GET", itemHref)).Content.ReadAsStringAsync());

        // ---- If-None-Match: * is "create only if absent" — the first-write race guard clients rely on ------
        var wouldClobber = await SendAsync(client, auth, "PUT", itemHref, Item(protocol, uid, "Clobber"), ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.PreconditionFailed, wouldClobber.StatusCode);
        Assert.Contains("Second title", await (await SendAsync(client, auth, "GET", itemHref)).Content.ReadAsStringAsync());

        // On a name that is free, the same header is how a client safely creates.
        var freshUid = $"w-{Guid.NewGuid():N}";
        var freshCreate = await SendAsync(client, auth, "PUT", $"{collectionHref}{freshUid}{protocol.Extension}",
            Item(protocol, freshUid, "Fresh"), ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.Created, freshCreate.StatusCode);

        // ---- DELETE soft-deletes: gone from the collection, present in the recycle bin ----------------------
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, auth, "DELETE", itemHref)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, auth, "GET", itemHref)).StatusCode);

        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var recycleHref = personal.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "recycle-bin").ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? null
                : personal.GetProperty("links").EnumerateArray()
                    .First(l => l.GetProperty("rel").GetString() == "recycle-bin").GetProperty("href").GetString();
        if (recycleHref is not null)
        {
            var bin = await TestJson.Get(api, recycleHref);
            Assert.Contains(bin.GetProperty("items").EnumerateArray(), i => i.GetProperty("name").GetString()!.Contains("Second title", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_reader_cannot_write_and_the_privilege_set_says_so(string protocolName)
    {
        var protocol = Of(protocolName);
        var (mine, myAuth, myApi) = await SeedAsync();
        var (theirs, theirAuth, _) = await SeedAsync();
        using var _1 = mine;
        using var _2 = myApi;
        using var _3 = theirs;

        // My own collection reports write privilege …
        var listing = await SendAsync(mine, myAuth, "PROPFIND", $"{protocol.Base}/{protocol.Collections}/");
        var privileges = XDocument.Parse(await listing.Content.ReadAsStringAsync())
            .Descendants(Dav + "privilege").Elements().Select(e => e.Name.LocalName).ToList();
        // bind/unbind matter as much as write: a client checks BIND before offering "new item" and UNBIND
        // before offering delete, so reporting only write leaves a capable client read-only (ADR 0621).
        Assert.Contains("write", privileges);
        Assert.Contains("bind", privileges);
        Assert.Contains("unbind", privileges);

        // … and someone else's collection is not addressable at all (404, not an empty listing).
        var theirCollection = await CollectionHrefAsync(theirs, theirAuth, protocol);
        var uid = $"x-{Guid.NewGuid():N}";
        var forbidden = await SendAsync(mine, myAuth, "PUT", $"{theirCollection}{uid}{protocol.Extension}", Item(protocol, uid, "Nope"));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Fact]
    public async Task A_typed_folder_can_be_created_anywhere_and_carries_a_colour()
    {
        var (client, auth, api) = await SeedAsync();
        using var _1 = client;
        using var _2 = api;

        // Create a Calendar inside the personal repository — the fold-down "New Calendar" path.
        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var childrenHref = personal.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "children").GetProperty("href").GetString()!;
        var name = $"Team calendar {Guid.NewGuid():N}"[..20];
        var created = await TestJson.Post(api, childrenHref, new { name, folderMask = "calendar" });
        var folderId = created.GetProperty("id").GetGuid();

        // It shows up as a CalDAV collection — which only happens if it really wears the Calendar mask.
        var listing = await SendAsync(client, auth, "PROPFIND", "/caldav/calendars/");
        var doc = XDocument.Parse(await listing.Content.ReadAsStringAsync());
        Assert.Contains(doc.Descendants(Dav + "displayname").Select(e => e.Value), d => d.EndsWith(name, StringComparison.Ordinal));

        // An unknown folder kind is the caller's mistake, not a plain folder.
        using var bad = new HttpRequestMessage(HttpMethod.Post, childrenHref)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { name = $"x{Guid.NewGuid():N}"[..8], folderMask = "spreadsheet" }),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(bad)).StatusCode);

        // The caller's personal colour override rides on the document's advertised rel, and reaches the
        // protocol as the colour a calendar client reads.
        var document = await TestJson.Get(api, $"/api/documents/{folderId}");
        var colorHref = document.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "collection-color").GetProperty("href").GetString()!;
        using var put = new HttpRequestMessage(HttpMethod.Put, colorHref)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { color = "#3f51b5" }),
        };
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(put)).StatusCode);

        var colored = await SendAsync(client, auth, "PROPFIND", "/caldav/calendars/");
        Assert.Contains("#3f51b5", await colored.Content.ReadAsStringAsync());

        // Reset is a delete — the collection's own default applies again (there is none here, so no colour).
        using var reset = new HttpRequestMessage(HttpMethod.Delete, colorHref);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(reset)).StatusCode);
        Assert.DoesNotContain("#3f51b5", await (await SendAsync(client, auth, "PROPFIND", "/caldav/calendars/")).Content.ReadAsStringAsync());
    }
}
