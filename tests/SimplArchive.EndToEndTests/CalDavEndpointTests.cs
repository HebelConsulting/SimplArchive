using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SimplArchive.EndToEndTests;

// CalDAV + CardDAV, slice 1 (#564, ADR 0619) — read-only, driven over raw HTTP the way a client does:
// .well-known discovery → principal → home set → collection → item. Both protocols run the SAME machinery
// (one DavProtocol descriptor each), so the tests are a [Theory] over the pair: a divergence between them
// is a bug by construction, and testing only one would hide it.
[Collection(E2ECollection.Name)]
public class CalDavEndpointTests
{
    private static readonly XNamespace Dav = "DAV:";

    private readonly E2EApiFactory _factory;

    public CalDavEndpointTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Protocol(string Base, string Collections, string Extension, string DefaultFolder, string ContentType);

    private static readonly Protocol CalDav = new("/caldav", "calendars", ".ics", "My Calendar", "text/calendar");
    private static readonly Protocol CardDav = new("/carddav", "addressbooks", ".vcf", "My Addressbook", "text/vcard");

    public static TheoryData<string> Protocols => ["caldav", "carddav"];

    private static Protocol Of(string name) => name == "caldav" ? CalDav : CardDav;

    private static byte[] Item(Protocol protocol, string uid, string title) => Encoding.UTF8.GetBytes(
        protocol == CalDav
            ? $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SimplArchive//E2E//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\n"
              + $"DTSTAMP:20260817T090000Z\r\nDTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\n"
              + $"SUMMARY:{title}\r\nLOCATION:Room 1\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
            : $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:{title}\r\nN:{title};;;;\r\n"
              + $"EMAIL:{title.Replace(" ", ".").ToLowerInvariant()}@example.test\r\nTEL:+41 44 000 00 00\r\nORG:Contoso\r\nEND:VCARD\r\n");

    private async Task<(HttpClient Client, AuthenticationHeaderValue Auth, string Email)> SeedAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Get-or-create the personal repository — that is what provisions My Calendar / My Addressbook.
        await TestJson.Post(api, "/api/me/personal-repository", new { });
        var generated = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{generated.GetProperty("password").GetString()}")));
        return (_factory.CreateClient(), auth, email);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, AuthenticationHeaderValue auth, string method, string url, string? body = null, string depth = "1")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Headers = { Authorization = auth } };
        request.Headers.TryAddWithoutValidation("Depth", depth);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        }

        return await client.SendAsync(request);
    }

    private static async Task<XDocument> MultiStatusAsync(HttpResponseMessage response)
    {
        Assert.Equal(207, (int)response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Discovery_principal_home_set_and_collection_listing(string protocolName)
    {
        var protocol = Of(protocolName);
        var (client, auth, _) = await SeedAsync();
        using var _1 = client;

        // ---- .well-known discovery is answered WITHOUT credentials (a client probes it first) -------------
        using var noRedirect = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var wellKnown = await noRedirect.GetAsync($"/.well-known/{protocolName}");
        Assert.Equal(HttpStatusCode.MovedPermanently, wellKnown.StatusCode);
        Assert.Equal($"{protocol.Base}/", wellKnown.Headers.Location!.ToString());

        // ---- Unauthenticated PROPFIND is challenged --------------------------------------------------------
        using var anonymous = _factory.CreateClient();
        using var challenge = new HttpRequestMessage(new HttpMethod("PROPFIND"), protocol.Base);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(challenge)).StatusCode);

        // ---- The root names the principal ------------------------------------------------------------------
        var root = await MultiStatusAsync(await SendAsync(client, auth, "PROPFIND", protocol.Base, depth: "0"));
        var principalHref = root.Descendants(Dav + "current-user-principal").Descendants(Dav + "href").Single().Value;
        Assert.StartsWith($"{protocol.Base}/principals/", principalHref);

        // ---- The principal names the home set --------------------------------------------------------------
        var principal = await MultiStatusAsync(await SendAsync(client, auth, "PROPFIND", principalHref, depth: "0"));
        var homeSetHref = principal.Descendants()
            .Where(e => e.Name.LocalName.EndsWith("home-set", StringComparison.Ordinal))
            .Descendants(Dav + "href").Single().Value;
        Assert.Equal($"{protocol.Base}/{protocol.Collections}/", homeSetHref);

        // ---- The home set lists the personal default collection FIRST --------------------------------------
        var homeSet = await MultiStatusAsync(await SendAsync(client, auth, "PROPFIND", homeSetHref));
        var displayNames = homeSet.Descendants(Dav + "displayname").Select(e => e.Value).ToList();
        Assert.Contains(displayNames, n => n.EndsWith(protocol.DefaultFolder, StringComparison.Ordinal));

        // It is typed as this protocol's collection kind (calendar / addressbook), which is what makes a
        // client offer it for subscription at all.
        Assert.Contains(homeSet.Descendants(Dav + "resourcetype").Elements(),
            e => e.Name.LocalName is "calendar" or "addressbook");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task An_uploaded_item_is_classified_listed_and_served(string protocolName)
    {
        var protocol = Of(protocolName);
        var (client, auth, _) = await SeedAsync();
        using var _1 = client;

        // Find the personal default collection through the protocol itself (never a composed URL).
        var homeSet = await MultiStatusAsync(await SendAsync(client, auth, "PROPFIND", $"{protocol.Base}/{protocol.Collections}/"));
        var collectionHref = homeSet.Descendants(Dav + "response")
            .Single(r => r.Descendants(Dav + "displayname").Any(d => d.Value.EndsWith(protocol.DefaultFolder, StringComparison.Ordinal)))
            .Element(Dav + "href")!.Value;

        // ---- File an item through WebDAV — the SAME shared credential, and the path a user would use -------
        var uid = $"e2e-{Guid.NewGuid():N}";
        var title = protocol == CalDav ? "Quarterly review" : "Ada Lovelace";
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/Personal/{protocol.DefaultFolder}/{uid}{protocol.Extension}")
        {
            Content = new ByteArrayContent(Item(protocol, uid, title)),
            Headers = { Authorization = auth },
        })
        {
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(put)).StatusCode);
        }

        // ---- It appears in the collection, named from its UID ----------------------------------------------
        var listing = await MultiStatusAsync(await SendAsync(client, auth, "PROPFIND", collectionHref));
        var itemHref = listing.Descendants(Dav + "href").Select(e => e.Value)
            .Single(h => h.EndsWith(Uri.EscapeDataString(uid + protocol.Extension), StringComparison.Ordinal));

        // ---- GET serves the stored bytes with the protocol's media type ------------------------------------
        using var get = new HttpRequestMessage(HttpMethod.Get, itemHref) { Headers = { Authorization = auth } };
        var served = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(protocol.ContentType, served.Content.Headers.ContentType!.MediaType);
        var body = await served.Content.ReadAsStringAsync();
        Assert.Contains(uid, body);
        Assert.Contains(title, body);
        Assert.False(string.IsNullOrWhiteSpace(served.Headers.ETag?.Tag));

        // ---- The multiget REPORT carries the data inline (what a syncing client actually uses) --------------
        var report = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <R:{(protocol == CalDav ? "calendar" : "addressbook")}-multiget xmlns:D="DAV:" xmlns:R="{(protocol == CalDav ? "urn:ietf:params:xml:ns:caldav" : "urn:ietf:params:xml:ns:carddav")}">
              <D:prop><D:getetag/><R:{(protocol == CalDav ? "calendar-data" : "address-data")}/></D:prop>
              <D:href>{itemHref}</D:href>
            </R:{(protocol == CalDav ? "calendar" : "addressbook")}-multiget>
            """;
        // NOTE the property name: the CardDAV report is `addressbook-multiget` but its data property is
        // `address-data` (RFC 6352) — not `addressbook-data`. The pre-port middleware derived the name from the
        // collection type and so emitted a property no CardDAV client reads; the ported DavNames has it right.
        var multiget = await MultiStatusAsync(await SendAsync(client, auth, "REPORT", collectionHref, report));
        var data = multiget.Descendants()
            .Single(e => e.Name.LocalName is "calendar-data" or "address-data").Value;
        Assert.Contains(uid, data);

        // Reaching here already proves classification ran: the typed-folder containment invariant REFUSES a
        // child that does not wear the item mask, so the WebDAV PUT above could not have been saved otherwise.
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_collection_the_caller_cannot_see_is_not_listed_and_not_readable(string protocolName)
    {
        var protocol = Of(protocolName);
        var (mine, myAuth, _) = await SeedAsync();
        var (theirs, theirAuth, _) = await SeedAsync();
        using var _1 = mine;
        using var _2 = theirs;

        // The other user's personal default collection, addressed from THEIR listing.
        var theirHomeSet = await MultiStatusAsync(await SendAsync(theirs, theirAuth, "PROPFIND", $"{protocol.Base}/{protocol.Collections}/"));
        var theirCollectionHref = theirHomeSet.Descendants(Dav + "response")
            .Single(r => r.Descendants(Dav + "displayname").Any(d => d.Value.EndsWith(protocol.DefaultFolder, StringComparison.Ordinal)))
            .Element(Dav + "href")!.Value;

        // It is absent from my home set …
        var myHomeSet = await MultiStatusAsync(await SendAsync(mine, myAuth, "PROPFIND", $"{protocol.Base}/{protocol.Collections}/"));
        Assert.DoesNotContain(theirCollectionHref, myHomeSet.Descendants(Dav + "href").Select(e => e.Value));

        // … and addressing it directly is a 404 rather than a listing (the ACL decides, not the URL).
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(mine, myAuth, "PROPFIND", theirCollectionHref)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Structure_changing_verbs_are_refused(string protocolName)
    {
        // Slice 2 made items writable (PUT/DELETE — CalDavWriteTests), but the archive TREE is still shaped in
        // the app, not by a sync client: creating, moving or renaming a collection over the protocol stays
        // refused, the same rule the IMAP endpoint applies to mailboxes.
        var protocol = Of(protocolName);
        var (client, auth, _) = await SeedAsync();
        using var _1 = client;

        var collection = $"{protocol.Base}/{protocol.Collections}/{Guid.NewGuid()}/";
        foreach (var method in new[] { "MKCOL", "MOVE" })
        {
            var response = await SendAsync(client, auth, method, collection);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        // PROPPATCH is the deliberate exception: it is ACKNOWLEDGED and ignored, never refused. Apple's
        // dataaccessd sets collection metadata during account setup and aborts on a 405, so refusing it means
        // the account never finishes (ADR 0621 — the port's most consequential fix).
        var propPatch = await SendAsync(client, auth, "PROPPATCH", collection,
            """<?xml version="1.0" encoding="utf-8"?><D:propertyupdate xmlns:D="DAV:"><D:set><D:prop><D:displayname>x</D:displayname></D:prop></D:set></D:propertyupdate>""");
        Assert.Equal(207, (int)propPatch.StatusCode);

        // A PUT addressed at the COLLECTION rather than an item inside it is equally not a thing.
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await SendAsync(client, auth, "PUT", collection, "x")).StatusCode);
    }

    // A REPORT we do not implement must be REFUSED, not answered with something plausible (#595, ADR 0626).
    //
    // Before this, ReportAsync handled sync-collection and then treated every other body identically — so an
    // unrecognised report fell through and was answered with a 207 full of collection data. The client believed
    // it had succeeded, which is the exact shape ADR 0626 exists to forbid: a wrong answer that looks like a
    // right one, with nothing in the log to say otherwise.
    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task An_unsupported_report_is_refused_with_the_precondition_that_says_why(string protocolName)
    {
        var protocol = Of(protocolName);
        var (client, auth, _) = await SeedAsync();
        using var _1 = client;

        var collectionHref = await DefaultCollectionAsync(client, auth, protocol);

        // free-busy-query is real CalDAV (RFC 4791 §7.10) and we do not implement it — the honest case. Its
        // response shape is an iCalendar body, so answering with a multistatus would be nonsense a client
        // cannot detect.
        var response = await SendAsync(client, auth, "REPORT", collectionHref,
            """<?xml version="1.0"?><C:free-busy-query xmlns:C="urn:ietf:params:xml:ns:caldav"><C:time-range start="20260101T000000Z" end="20261231T000000Z"/></C:free-busy-query>""");

        // 403 with the DAV:supported-report precondition (RFC 3253 §3.6) — which rule was broken, not merely
        // that something was.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Dav + "error", error.Root!.Name);
        Assert.Single(error.Root.Elements(Dav + "supported-report"));
    }

    // The other direction, and the one that keeps the refusal honest: EVERY report we advertise must actually
    // work. A server that advertises a capability it refuses is worse than one that never advertised it, because
    // the client trusts the advertisement — "a capability we advertise is a promise" (ADR 0626).
    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Every_advertised_report_is_actually_served(string protocolName)
    {
        var protocol = Of(protocolName);
        var (client, auth, _) = await SeedAsync();
        using var _1 = client;

        var collectionHref = await DefaultCollectionAsync(client, auth, protocol);

        // Read the advertisement from the server rather than restating it here — a copy in the test would keep
        // passing after the server's list changed, which is the failure this test exists to catch.
        var props = await MultiStatusAsync(await SendAsync(client, auth, "PROPFIND", collectionHref, depth: "0"));
        var advertised = props.Descendants(Dav + "supported-report")
            .Select(r => r.Element(Dav + "report")?.Elements().FirstOrDefault()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        Assert.NotEmpty(advertised);

        foreach (var report in advertised)
        {
            // A minimal, valid body for each: enough to be dispatched, nothing that needs real data.
            var body = $"""<?xml version="1.0"?><R:{report.LocalName} xmlns:R="{report.NamespaceName}" xmlns:D="DAV:"><D:prop><D:getetag/></D:prop></R:{report.LocalName}>""";
            var response = await SendAsync(client, auth, "REPORT", collectionHref, body);

            Assert.True(
                response.StatusCode != HttpStatusCode.Forbidden,
                $"{protocol.Base} advertises {report} in supported-report-set but refuses it with 403");
        }
    }

    private static async Task<string> DefaultCollectionAsync(HttpClient client, AuthenticationHeaderValue auth, Protocol protocol)
    {
        var homeSet = await MultiStatusAsync(await SendAsync(client, auth, "PROPFIND", $"{protocol.Base}/{protocol.Collections}/"));
        return homeSet.Descendants(Dav + "response")
            .Single(r => r.Descendants(Dav + "displayname").Any(d => d.Value.EndsWith(protocol.DefaultFolder, StringComparison.Ordinal)))
            .Element(Dav + "href")!.Value;
    }
}
