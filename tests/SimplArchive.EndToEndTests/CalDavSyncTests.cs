using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SimplArchive.EndToEndTests;

// RFC 6578 sync-collection and WebDAV-Push registration (#564 slice 3, ADR 0622), driven over raw HTTP the way
// DAVx⁵ and Apple's clients do. A [Theory] over both protocols, since one implementation serves both.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class CalDavSyncTests
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace Push = "https://bitfire.at/webdav-push";
    private static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";

    private readonly E2EApiFactory _factory;

    public CalDavSyncTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Protocol(string Base, string Collections, string Extension, string DefaultFolder);

    private static readonly Protocol CalDav = new("/caldav", "calendars", ".ics", "My Calendar");
    private static readonly Protocol CardDav = new("/carddav", "addressbooks", ".vcf", "My Addressbook");

    public static TheoryData<string> Protocols => ["caldav", "carddav"];

    private static Protocol Of(string name) => name == "caldav" ? CalDav : CardDav;

    private static string Item(Protocol protocol, string uid, string title) =>
        protocol == CalDav
            ? $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SimplArchive//E2E//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\n"
              + $"DTSTAMP:20260817T090000Z\r\nDTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\nSUMMARY:{title}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
            : $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:{title}\r\nN:{title};;;;\r\nEND:VCARD\r\n";

    private async Task<(HttpClient Client, AuthenticationHeaderValue Auth)> SeedAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"davs-{Guid.NewGuid():N}@e2e.local";
        const string password = "davs-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav Sync");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });
        var generated = await TestJson.Post(api, "/api/me/webdav-password", new { });
        return (_factory.CreateClient(), new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{generated.GetProperty("password").GetString()}"))));
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

    private static async Task<string> CollectionHrefAsync(HttpClient client, AuthenticationHeaderValue auth, Protocol protocol)
    {
        var response = await SendAsync(client, auth, "PROPFIND", $"{protocol.Base}/{protocol.Collections}/");
        return XDocument.Parse(await response.Content.ReadAsStringAsync())
            .Descendants(Dav + "response")
            .Single(r => r.Descendants(Dav + "displayname").Any(d => d.Value.EndsWith(protocol.DefaultFolder, StringComparison.Ordinal)))
            .Element(Dav + "href")!.Value;
    }

    private static string SyncBody(string? token) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <D:sync-collection xmlns:D="DAV:">
          <D:sync-token>{token}</D:sync-token>
          <D:sync-level>1</D:sync-level>
          <D:prop><D:getetag/></D:prop>
        </D:sync-collection>
        """;

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Sync_reports_only_what_changed_and_names_removals(string protocolName)
    {
        var protocol = Of(protocolName);
        var (client, auth) = await SeedAsync();
        using var _1 = client;
        var collectionHref = await CollectionHrefAsync(client, auth, protocol);

        // ---- The collection advertises a CTag and a sync-token before anything happened -------------------
        var initial = XDocument.Parse(await (await SendAsync(client, auth, "PROPFIND", collectionHref, depth: "0")).Content.ReadAsStringAsync());
        Assert.NotNull(initial.Descendants(CalendarServer + "getctag").FirstOrDefault());
        var startToken = initial.Descendants(Dav + "sync-token").Single().Value;

        // ---- Two items in, then sync from the starting token ----------------------------------------------
        var keptUid = $"s-{Guid.NewGuid():N}";
        var doomedUid = $"s-{Guid.NewGuid():N}";
        foreach (var uid in new[] { keptUid, doomedUid })
        {
            var put = await SendAsync(client, auth, "PUT", $"{collectionHref}{uid}{protocol.Extension}", Item(protocol, uid, uid));
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        var afterCreates = XDocument.Parse(await (await SendAsync(client, auth, "REPORT", collectionHref, SyncBody(startToken))).Content.ReadAsStringAsync());
        var created = afterCreates.Descendants(Dav + "response")
            .Where(r => !r.Elements(Dav + "status").Any(s => s.Value.Contains("404")))
            .Select(r => r.Element(Dav + "href")!.Value).ToList();
        Assert.Equal(2, created.Count);
        var midToken = afterCreates.Descendants(Dav + "sync-token").Single().Value;
        Assert.NotEqual(startToken, midToken);

        // ---- Delete one; the next sync reports ONLY that, and as a 404 href -------------------------------
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(client, auth, "DELETE", $"{collectionHref}{doomedUid}{protocol.Extension}")).StatusCode);

        var afterDelete = XDocument.Parse(await (await SendAsync(client, auth, "REPORT", collectionHref, SyncBody(midToken))).Content.ReadAsStringAsync());
        var responses = afterDelete.Descendants(Dav + "response").ToList();
        Assert.Single(responses); // the untouched item is NOT re-sent — that is the whole point of a sync token
        Assert.Contains("404", responses[0].Element(Dav + "status")!.Value);
        Assert.Contains(doomedUid, responses[0].Element(Dav + "href")!.Value);

        // ---- A token we never issued must NOT be treated as "everything" ---------------------------------
        var foreign = await SendAsync(client, auth, "REPORT", collectionHref, SyncBody("https://example.invalid/ns/sync/99"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Contains("valid-sync-token", await foreign.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Push_registration_round_trips_and_the_collection_advertises_it(string protocolName)
    {
        var protocol = Of(protocolName);
        var (client, auth) = await SeedAsync();
        using var _1 = client;
        var collectionHref = await CollectionHrefAsync(client, auth, protocol);

        // The transport is advertised with a VAPID key and a topic that is NOT the folder id (it travels to a
        // third-party push service). In tests the key is the ephemeral Development one.
        var props = XDocument.Parse(await (await SendAsync(client, auth, "PROPFIND", collectionHref, depth: "0")).Content.ReadAsStringAsync());
        Assert.NotNull(props.Descendants(Push + "vapid-public-key").FirstOrDefault());
        var topic = props.Descendants(Push + "topic").Single().Value;
        Assert.False(string.IsNullOrWhiteSpace(topic));
        Assert.DoesNotContain(collectionHref.Split('/')[^2], topic);

        var register = """
            <?xml version="1.0" encoding="utf-8"?>
            <P:push-register xmlns:D="DAV:" xmlns:P="https://bitfire.at/webdav-push">
              <P:subscription>
                <P:web-push-subscription>
                  <P:push-resource>https://ntfy.example.invalid/up?id=abc123</P:push-resource>
                  <P:subscription-public-key>BF3xY2y7c1kQ0m9j0f4pQ2m8n7k6l5j4h3g2f1d0s9a8</P:subscription-public-key>
                  <P:auth-secret>c2VjcmV0LWF1dGgtdmFsdWU</P:auth-secret>
                </P:web-push-subscription>
              </P:subscription>
            </P:push-register>
            """;

        var registered = await SendAsync(client, auth, "POST", collectionHref, register);
        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        var location = registered.Headers.Location!.ToString();
        Assert.Contains("/dav/push-subscriptions/", location);
        // The server states the expiry it DECIDED, capping whatever the client asked for. Expires is an HTTP
        // CONTENT header, so HttpClient files it under Content.Headers even on a 204 — check both rather than
        // assume which side it lands on.
        Assert.True(
            registered.Headers.TryGetValues("Expires", out _) || registered.Content.Headers.TryGetValues("Expires", out _),
            "the registration response must state the expiry the server decided");

        // Re-registering the same endpoint UPDATES rather than duplicating — clients re-register routinely,
        // and a duplicate would deliver every notification twice.
        var again = await SendAsync(client, auth, "POST", collectionHref, register);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(location, again.Headers.Location!.ToString());

        // Unregistering is idempotent: a client retrying a delete must not get an error.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, auth, "DELETE", location)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, auth, "DELETE", location)).StatusCode);
    }
}
