using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The My tasks tab, on a phone (#650, slice 4 of #564). Reminder apps and DAVx⁵ both sync tasks over CalDAV as
// VTODO, so this is not a new protocol — it is a calendar collection whose component set says VTODO.
//
// Nothing is stored: the items are composed from WorkflowState at read time, so these assertions are also the
// only thing standing between the feed and a silent divergence from the tab it mirrors.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TaskFeedTests
{
    private readonly E2EApiFactory _factory;

    public TaskFeedTests(E2EApiFactory factory) => _factory = factory;

    private const string CalDavRoot = "/caldav";

    /// <summary>A user with a DAV password, and one document assigned to them for review.</summary>
    private async Task<(HttpClient Api, HttpClient Dav, AuthenticationHeaderValue Basic, Guid DocumentId, Guid VersionId, string DocumentName)> ReviewerAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"feed-{Guid.NewGuid():N}@e2e.local";
        const string password = "feed-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Feed Reviewer", canManageRepositories: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repositoryId = (await TestJson.Post(api, "/api/repositories", new { name = $"feed-{Guid.NewGuid():N}"[..18] }))
            .GetProperty("id").GetGuid();

        // A name with a comma on purpose: iCalendar reads an unescaped one as a value separator, so a client
        // would show a truncated summary — or reject the item outright.
        var documentName = $"Invoice {Guid.NewGuid().ToString("N")[..6]}, final";
        var (documentId, versionId) = await UploadAsync(api, repositoryId, documentName);

        // Submit it for review, assigned to the same user — the shape the My tasks tab lists.
        (await api.PostAsJsonAsync($"/api/documents/{documentId}/versions/{versionId}/workflow/submit",
            new { reviewerId = userId })).EnsureSuccessStatusCode();

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return (api, _factory.CreateClient(), basic, documentId, versionId, documentName);
    }

    /// <summary>A real document with a confirmed version — a workflow review needs one to hang off.</summary>
    private static async Task<(Guid DocumentId, Guid VersionId)> UploadAsync(HttpClient api, Guid parentId, string name)
    {
        var documentId = (await TestJson.Post(api, $"/api/documents/{parentId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{documentId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(name))))
                .EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{documentId}/versions/{versionId}", new { });
        return (documentId, versionId);
    }

    private async Task<string> PropfindAsync(HttpClient dav, AuthenticationHeaderValue basic, string path, int depth)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = basic } };
        request.Headers.Add("Depth", depth.ToString());
        request.Content = new StringContent(
            "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>", Encoding.UTF8, "text/xml");
        using var response = await dav.SendAsync(request);
        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Both_feeds_are_offered_and_the_task_one_announces_VTODO()
    {
        var (api, dav, basic, _, _, _) = await ReviewerAsync();
        using var _a = api;
        using var _d = dav;

        var home = await PropfindAsync(dav, basic, $"{CalDavRoot}/calendars/", depth: 1);

        Assert.Contains("My tasks", home, StringComparison.Ordinal);
        Assert.Contains("My task deadlines", home, StringComparison.Ordinal);

        // The component set is what makes this work at all: a reminder app ignores a VEVENT-only collection,
        // and a calendar app ignores a VTODO-only one. Advertising the wrong one means the feed is invisible in
        // exactly the app it was built for — and everything else would still look correct.
        Assert.Contains("VTODO", home, StringComparison.Ordinal);
        Assert.Contains("VEVENT", home, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_task_appears_as_a_VTODO_with_its_name_escaped()
    {
        var (api, dav, basic, _, _, documentName) = await ReviewerAsync();
        using var _a = api;
        using var _d = dav;

        var home = await PropfindAsync(dav, basic, $"{CalDavRoot}/calendars/", depth: 1);
        var feedHref = HrefOfFeed(home, "My tasks");

        var listing = await PropfindAsync(dav, basic, feedHref, depth: 1);
        var itemHref = ItemHrefs(listing).Single();

        using var get = new HttpRequestMessage(HttpMethod.Get, itemHref) { Headers = { Authorization = basic } };
        using var response = await dav.SendAsync(get);
        response.EnsureSuccessStatusCode();
        var ics = await response.Content.ReadAsStringAsync();

        Assert.Contains("BEGIN:VTODO", ics, StringComparison.Ordinal);
        Assert.Contains("STATUS:NEEDS-ACTION", ics, StringComparison.Ordinal);

        // The comma in the document name is escaped, and the SUMMARY still carries the name — the pair matters:
        // escaping that dropped the text would satisfy a naive "no bare comma" check while showing nothing.
        Assert.Contains("\\,", ics, StringComparison.Ordinal);
        Assert.Contains(documentName.Split(',')[0], ics, StringComparison.Ordinal);
        Assert.DoesNotContain($"SUMMARY:Review: {documentName}", ics, StringComparison.Ordinal);

        // CRLF and folded to 75 octets — both are what a strict parser requires and what nothing else checks.
        Assert.Contains("\r\n", ics, StringComparison.Ordinal);
        Assert.All(ics.Split("\r\n"), line => Assert.True(
            Encoding.UTF8.GetByteCount(line) <= 75, $"An unfolded line of {Encoding.UTF8.GetByteCount(line)} octets: {line}"));
    }

    [Fact]
    public async Task The_deadlines_feed_holds_only_tasks_that_HAVE_a_due_date()
    {
        // WorkflowState.DueAt is null unless the document's mask defines a review SLA, so a tenant with none
        // configured has an empty deadlines feed. That is correct rather than broken — and asserting it is what
        // stops someone "fixing" it later by emitting a VEVENT with no date, which clients place at the epoch.
        var (api, dav, basic, _, _, _) = await ReviewerAsync();
        using var _a = api;
        using var _d = dav;

        var home = await PropfindAsync(dav, basic, $"{CalDavRoot}/calendars/", depth: 1);

        var todos = ItemHrefs(await PropfindAsync(dav, basic, HrefOfFeed(home, "My tasks"), depth: 1));
        var deadlines = ItemHrefs(await PropfindAsync(dav, basic, HrefOfFeed(home, "My task deadlines"), depth: 1));

        Assert.Single(todos);
        Assert.Empty(deadlines);
    }

    [Fact]
    public async Task The_feeds_are_read_only()
    {
        // Completing a review happens in the workbench, where it has an actor, a comment and an audit trail. A
        // VTODO ticked off on a phone has none of those.
        //
        // DELETE is the one that matters: the item's "document id" is a WORKFLOW STATE id, so an unguarded
        // delete reaches the ACL walk with an id that has no document row — which throws rather than refusing.
        var (api, dav, basic, _, _, _) = await ReviewerAsync();
        using var _a = api;
        using var _d = dav;

        var home = await PropfindAsync(dav, basic, $"{CalDavRoot}/calendars/", depth: 1);
        var feedHref = HrefOfFeed(home, "My tasks");
        var itemHref = ItemHrefs(await PropfindAsync(dav, basic, feedHref, depth: 1)).Single();

        using var put = new HttpRequestMessage(HttpMethod.Put, itemHref) { Headers = { Authorization = basic } };
        put.Content = new StringContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", Encoding.UTF8, "text/calendar");
        using var putResponse = await dav.SendAsync(put);
        Assert.Equal(HttpStatusCode.Forbidden, putResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, itemHref) { Headers = { Authorization = basic } };
        using var deleteResponse = await dav.SendAsync(delete);
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task The_feed_is_the_callers_own_and_nobody_elses()
    {
        // The collection id is derived from the user, so another user's feed URL must not resolve for this
        // caller — otherwise a URL that leaked once would expose somebody's workload indefinitely.
        var (api, dav, basic, _, _, _) = await ReviewerAsync();
        using var _a = api;
        using var _d = dav;

        var mine = HrefOfFeed(await PropfindAsync(dav, basic, $"{CalDavRoot}/calendars/", depth: 1), "My tasks");

        var (otherApi, otherDav, otherBasic, _, _, _) = await ReviewerAsync();
        using var _oa = otherApi;
        using var _od = otherDav;

        var theirs = HrefOfFeed(await PropfindAsync(otherDav, otherBasic, $"{CalDavRoot}/calendars/", depth: 1), "My tasks");
        Assert.NotEqual(mine, theirs);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), theirs) { Headers = { Authorization = basic } };
        request.Headers.Add("Depth", "0");
        using var response = await dav.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_CTag_moves_when_the_tasks_change()
    {
        // A polling client re-reads only when the CTag differs, so one that never moves means a phone showing a
        // stale task list forever — the failure mode a subscriber cannot detect from their side.
        var (api, dav, basic, documentId, versionId, _) = await ReviewerAsync();
        using var _a = api;
        using var _d = dav;

        var before = CTagOf(await PropfindAsync(dav, basic, HrefOfFeed(
            await PropfindAsync(dav, basic, $"{CalDavRoot}/calendars/", depth: 1), "My tasks"), depth: 0));

        // Approving it takes the review out of "in review", so the item leaves the feed.
        (await api.PostAsJsonAsync($"/api/documents/{documentId}/versions/{versionId}/workflow/approve", new { }))
            .EnsureSuccessStatusCode();

        var feedHref = HrefOfFeed(await PropfindAsync(dav, basic, $"{CalDavRoot}/calendars/", depth: 1), "My tasks");
        var listing = await PropfindAsync(dav, basic, feedHref, depth: 1);

        Assert.Empty(ItemHrefs(listing));
        Assert.NotEqual(before, CTagOf(listing));
    }

    /// <summary>The collection href a multistatus advertises for the feed with this display name.</summary>
    private static string HrefOfFeed(string multistatus, string displayName)
    {
        var document = System.Xml.Linq.XDocument.Parse(multistatus);
        System.Xml.Linq.XNamespace dav = "DAV:";
        var response = document.Descendants(dav + "response")
            .Single(r => r.Descendants(dav + "displayname").Any(d => d.Value == displayName));
        return response.Element(dav + "href")!.Value;
    }

    /// <summary>The item hrefs in a collection listing — everything but the collection's own href.</summary>
    private static List<string> ItemHrefs(string multistatus)
    {
        var document = System.Xml.Linq.XDocument.Parse(multistatus);
        System.Xml.Linq.XNamespace dav = "DAV:";
        return [.. document.Descendants(dav + "response")
            .Select(r => r.Element(dav + "href")!.Value)
            .Where(h => h.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))];
    }

    private static string CTagOf(string multistatus)
    {
        var document = System.Xml.Linq.XDocument.Parse(multistatus);
        return document.Descendants().First(e => e.Name.LocalName == "getctag").Value;
    }
}
