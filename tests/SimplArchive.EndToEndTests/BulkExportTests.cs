using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace SimplArchive.EndToEndTests;

// The combined export (#658), over the wire: two CardDAV-filed contacts come back as ONE .vcf carrying both
// records with their UIDs untouched — the correlation keys a later sync matches on — and a selection that
// cannot combine is refused with the reason, because a file silently missing an item is worse than no file.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class BulkExportTests
{
    private readonly E2EApiFactory _factory;

    public BulkExportTests(E2EApiFactory factory) => _factory = factory;

    private static string Card(string uid) =>
        $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:Export Case {uid[^4..]}\r\nEND:VCARD\r\n";

    [Fact]
    public async Task Two_contacts_export_as_one_vcf_with_both_uids()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"export-{Guid.NewGuid():N}@e2e.local";
        const string password = "export-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Export User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        // The addressbook href is DISCOVERED (the ContactCardEndpointTests lesson: a guessed path 404s
        // beside five green assertions).
        using var probe = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/carddav/addressbooks/") { Headers = { Authorization = auth } };
        probe.Headers.TryAddWithoutValidation("Depth", "1");
        var listing = XDocument.Parse(await (await dav.SendAsync(probe)).Content.ReadAsStringAsync());
        XNamespace davNs = "DAV:";
        var collectionHref = listing.Descendants(davNs + "response")
            .Single(r => r.Descendants(davNs + "displayname").Any(d => d.Value.EndsWith("My Addressbook", StringComparison.Ordinal)))
            .Element(davNs + "href")!.Value;

        var uidA = $"uid-{Guid.NewGuid():N}";
        var uidB = $"uid-{Guid.NewGuid():N}";
        foreach (var uid in new[] { uidA, uidB })
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, $"{collectionHref}{uid}.vcf")
            {
                Content = new StringContent(Card(uid), Encoding.UTF8, "text/vcard"),
                Headers = { Authorization = auth },
            };
            Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        }

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var idA = await FindByUidAsync(api, personalId, uidA);
        var idB = await FindByUidAsync(api, personalId, uidB);

        // The export follows the bulk collection's own rel (ADR 0543).
        var bulk = await TestJson.Get(api, "/api/documents/bulk");
        var exportHref = bulk.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "export").GetProperty("href").GetString()!;

        using var response = await api.PostAsJsonAsync(exportHref, new { ids = new[] { idA, idB }, name = "My Addressbook" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/vcard", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("My Addressbook.vcf", response.Content.Headers.ContentDisposition?.FileName ?? response.Content.Headers.ContentDisposition?.FileNameStar ?? "");

        var combined = await response.Content.ReadAsStringAsync();
        Assert.Equal(2, combined.Split("BEGIN:VCARD").Length - 1);
        Assert.Contains($"UID:{uidA}", combined);
        Assert.Contains($"UID:{uidB}", combined);

        // …and a selection that cannot combine is refused with the REASON: a contact plus an APPOINTMENT —
        // both real, both readable, still not one file. Filed through the same proven DAV path.
        var calendarHref = listing.Descendants(davNs + "response")
            .Select(r => r.Element(davNs + "href")!.Value).First().Replace("/carddav/addressbooks/", "/caldav/calendars/");
        using var calProbe = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/caldav/calendars/") { Headers = { Authorization = auth } };
        calProbe.Headers.TryAddWithoutValidation("Depth", "1");
        var calListing = XDocument.Parse(await (await dav.SendAsync(calProbe)).Content.ReadAsStringAsync());
        var calCollection = calListing.Descendants(davNs + "response")
            .Single(r => r.Descendants(davNs + "displayname").Any(d => d.Value.EndsWith("My Calendar", StringComparison.Ordinal)))
            .Element(davNs + "href")!.Value;

        var eventUid = $"uid-{Guid.NewGuid():N}";
        using var putEvent = new HttpRequestMessage(HttpMethod.Put, $"{calCollection}{eventUid}.ics")
        {
            Content = new StringContent(
                $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//t//EN\r\nBEGIN:VEVENT\r\nUID:{eventUid}\r\n"
                + "DTSTART:20260901T200000Z\r\nSUMMARY:Mixed case\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
                Encoding.UTF8, "text/calendar"),
            Headers = { Authorization = auth },
        };
        Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync(putEvent)).StatusCode);
        var eventId = await FindByUidAsync(api, personalId, eventUid, "Event UID");

        using var mixed = await api.PostAsJsonAsync(exportHref, new { ids = new[] { idA, eventId }, name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Contains("BULK_EXPORT_NOT_COMBINABLE", await mixed.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> FindByUidAsync(HttpClient api, Guid rootId, string uid, string fieldName = "Contact UID")
    {
        foreach (var (id, _) in await WalkAsync(api, rootId))
        {
            var indexData = await TestJson.Get(api, $"/api/documents/{id}/index-data");
            if (indexData.GetProperty("fields").EnumerateArray().Any(f =>
                    f.GetProperty("fieldName").GetString() == fieldName
                    && f.GetProperty("values").EnumerateArray().Any(v => v.GetString() == uid)))
            {
                return id;
            }
        }

        throw new InvalidOperationException($"item {uid} not found");
    }

    private static async Task<List<(Guid Id, string Name)>> WalkAsync(HttpClient api, Guid rootId)
    {
        var found = new List<(Guid, string)>();
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.TryDequeue(out var folder))
        {
            foreach (var child in (await TestJson.Get(api, $"/api/documents/{folder}/children")).GetProperty("children").EnumerateArray())
            {
                var id = child.GetProperty("id").GetGuid();
                found.Add((id, child.GetProperty("name").GetString()!));
                if (child.GetProperty("hasChildren").GetBoolean() || child.GetProperty("hasSubfolders").GetBoolean())
                {
                    queue.Enqueue(id);
                }
            }
        }

        return found;
    }
}
