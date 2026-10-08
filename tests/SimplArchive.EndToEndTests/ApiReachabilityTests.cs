using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// Every API endpoint is REACHABLE by following advertised links from <c>/api</c> (#1635, ADR 0543).
/// </summary>
/// <remarks>
/// <para>
/// The rest of this suite calls endpoints by composed URL, on purpose: it tests behaviour. That is exactly why it
/// cannot see a MISSING rel; it just calls the endpoint. Only rel-following consumers (the clients, saconsole, the
/// demo seeders) exercise reachability, and they are what kept finding these defects: a pilot never offered
/// <i>Sign</i>, nothing fileable into a module's exclusive folder.
/// </para>
/// <para>
/// This test crawls the API once, as several principals, following only what the server advertises: GET and HEAD
/// links are fetched breadth-first within a budget, other methods are recorded without being invoked. Every
/// endpoint in the route table must be matched by some advertised link. The exceptions are a justified list:
/// protocol endpoints and the root (<see cref="Protocol"/>), plus the triaged debt (<see cref="Unreached"/>),
/// which only shrinks.
/// </para>
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ApiReachabilityTests(E2EApiFactory factory)
{
    private const int BudgetPerPrincipal = 6000;
    // Generous on purpose: every folder shares the `children` template, so a small cap explored four folders and
    // missed whatever lay in the fifth (a notebook's rels, the first time this ran).
    private const int InstancesPerTemplate = 60;

    /// <summary>
    /// Route prefixes that are not reached through /api's hypermedia BY DESIGN, each with its reason. Only protocol
    /// surfaces and the root belong here (ADR 0908); a route that a client should reach by a rel never does.
    /// </summary>
    private static readonly (string Prefix, string Reason)[] Protocol =
    [
        ("api", "the root itself, the one address a client may know (ADR 0543); matched exactly"),
        ("api/theme.css", "a stylesheet the server's own HTML pages reference with <link rel=stylesheet>, not an API resource"),
        ("api/encrypted-content", "the token door an encrypted object's presigned URL is swapped for (ADR 0818): handed out as a URL, like a presign"),
        ("api/external-links/{token", "the public page an EXTERNAL recipient opens from the link they were sent (ADR 0827); no API principal"),
        ("api/diagnostics/throw-", "fixtures that throw on purpose, for testing the error pipeline"),
        ("api/test-module/", "the TEST module's own fixture routes; not part of the product, and this tenant does not activate it"),
    ];

    private static bool IsProtocol(string endpoint)
    {
        var template = endpoint[(endpoint.IndexOf(' ') + 1)..];
        return Protocol.Any(p => p.Prefix == "api" ? template == "api" : template.StartsWith(p.Prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Triaged endpoints no principal reaches through a rel, each with its reason or its issue. Only shrinks: an
    /// entry that becomes reachable fails <see cref="The_unreached_list_names_only_what_is_still_unreached"/>.
    /// </summary>
    /// <remarks>
    /// Two kinds, and the reason says which. Most need STATE the seed does not create yet, and their rels exist; a few
    /// are advertised by no rel at all and carry an issue. Growing the seed or adding the rel removes the entry.
    /// </remarks>
    private static readonly Dictionary<string, string> Unreached = new(StringComparer.Ordinal)
    {
        ["DELETE api/documents/{documentId:guid}/origin"] = "#1665: importer-only, advertised by no rel; the Porter composes it",
        ["GET api/documents/by-origin/{originTenantId:guid}/{originDocumentId:guid}"] = "#1665: importer-only, advertised by no rel; the Porter composes it",
        ["GET api/documents/{documentId:guid}/archive-entries"] = "needs a zip document; the seed files none",
        ["GET api/documents/{documentId:guid}/archive-entries/content"] = "needs a zip document; the seed files none",
        ["GET api/documents/{documentId:guid}/checkout/preview"] = "needs a checked-out document with a renderable working copy; the seed checks out plain text",
        ["GET api/documents/{documentId:guid}/checkout/working-copy/pages"] = "needs a checked-out multi-page scan; the seed checks out plain text",
        ["GET api/documents/{documentId:guid}/contact-card/photo"] = "needs a contact with a photo",
        ["GET api/documents/{documentId:guid}/external-links/{linkId:guid}/url"] = "advertised only when the tenant shows link URLs, which the seed leaves off",
        ["GET api/documents/{documentId:guid}/machine/{machineId}/proposals/{proposalName}"] = "needs an active module with a state machine; no module is active in this tenant",
        ["GET api/documents/{documentId:guid}/origin"] = "#1665: importer-only, advertised by no rel; the Porter composes it",
        ["GET api/documents/{documentId:guid}/versions/{versionId:guid}/enveloped-content"] = "needs a Strict-tier tenant (ADR 0825)",
        ["GET api/intray/{name}/pages"] = "needs an ingested scan in the intray; the seed uploads plain text that is not ingested yet",
        ["GET api/modules/{moduleId}/projections"] = "needs an active module with a read model; no module is active in this tenant",
        ["HEAD api/documents/by-origin/{originTenantId:guid}/{originDocumentId:guid}"] = "#1665: importer-only, advertised by no rel; the Porter composes it",
        ["HEAD api/documents/{documentId:guid}/archive-entries"] = "needs a zip document; the seed files none",
        ["HEAD api/documents/{documentId:guid}/archive-entries/content"] = "needs a zip document; the seed files none",
        ["HEAD api/documents/{documentId:guid}/checkout/preview"] = "needs a checked-out document with a renderable working copy; the seed checks out plain text",
        ["HEAD api/documents/{documentId:guid}/checkout/working-copy/pages"] = "needs a checked-out multi-page scan; the seed checks out plain text",
        ["HEAD api/documents/{documentId:guid}/contact-card/photo"] = "needs a contact with a photo",
        ["HEAD api/documents/{documentId:guid}/external-links/{linkId:guid}/url"] = "advertised only when the tenant shows link URLs, which the seed leaves off",
        ["HEAD api/documents/{documentId:guid}/machine/{machineId}/proposals/{proposalName}"] = "needs an active module with a state machine; no module is active in this tenant",
        ["HEAD api/documents/{documentId:guid}/origin"] = "#1665: importer-only, advertised by no rel; the Porter composes it",
        ["HEAD api/documents/{documentId:guid}/versions/{versionId:guid}/enveloped-content"] = "needs a Strict-tier tenant (ADR 0825)",
        ["HEAD api/intray/{name}/pages"] = "needs an ingested scan in the intray; the seed uploads plain text that is not ingested yet",
        ["HEAD api/modules/{moduleId}/projections"] = "needs an active module with a read model; no module is active in this tenant",
        ["POST api/documents/{documentId:guid}/machine/{machineId}/transitions/{transitionName}"] = "needs an active module with a state machine; no module is active in this tenant",
        ["POST api/documents/{documentId:guid}/versions/{versionId:guid}/workflow/release"] = "needs an approved version",
        ["POST api/intray/previous/{name}/restore"] = "needs an intray item that was processed",
        ["POST api/intray/{name}/deskew"] = "needs an ingested scan in the intray; the seed uploads plain text that is not ingested yet",
        ["POST api/intray/{name}/pages/split"] = "needs an ingested scan in the intray; the seed uploads plain text that is not ingested yet",
        ["POST api/intray/{name}/patch-codes"] = "needs an ingested scan in the intray; the seed uploads plain text that is not ingested yet",
        ["POST api/intray/{name}/processed"] = "needs an ingested scan in the intray; the seed uploads plain text that is not ingested yet",
        ["POST api/modules/{moduleId}/rebuild/{projectionName}"] = "needs an active module with a read model; no module is active in this tenant",
        ["POST api/retention/{documentId:guid}/dispose"] = "needs a document past its retention",
        ["POST api/retention/{documentId:guid}/extend"] = "needs a document past its retention",
        ["POST api/sensitivity-labels/{id:guid}/unretire"] = "needs a retired label or tag",
        ["POST api/tags/{id:guid}/unretire"] = "needs a retired label or tag",
        ["PUT api/documents/{documentId:guid}/checkout/working-copy/pages/order"] = "needs a checked-out multi-page scan; the seed checks out plain text",
        ["PUT api/documents/{documentId:guid}/origin"] = "#1665: importer-only, advertised by no rel; the Porter composes it",
        ["PUT api/documents/{documentId:guid}/versions/{versionId:guid}/searchable"] = "needs a scan that can be made searchable",
        ["PUT api/intray/{name}/pages/order"] = "needs an ingested scan in the intray; the seed uploads plain text that is not ingested yet",
    };

    private sealed record Link(string Href, string Method);

    [Fact]
    public async Task Every_api_endpoint_is_reached_by_following_advertised_links()
    {
        var (covered, requests) = await CrawlAsync();
        var missing = Endpoints()
            .Where(e => !covered.Contains(e) && !IsProtocol(e) && !Unreached.ContainsKey(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"{missing.Count} endpoint(s) are not reached by following any advertised link from /api (ADR 0543), "
            + $"in {requests} requests. "
            + "A client can only reach what a rel leads to: advertise the rel that reaches it, or, for a "
            + "protocol endpoint, add it to Protocol with its reason:\n  " + string.Join("\n  ", missing));

        // The crawl must not reach what nothing advertises, or this test proves nothing: the diagnostics fixture is
        // linked from nowhere, so it has to be absent from what the crawl covered.
        Assert.DoesNotContain("GET api/diagnostics/throw-known-error", covered);
    }

    [Fact]
    public async Task The_unreached_list_names_only_what_is_still_unreached()
    {
        var (covered, _) = await CrawlAsync();
        var paid = Unreached.Keys.Where(covered.Contains).OrderBy(e => e, StringComparer.Ordinal).ToList();
        Assert.True(paid.Count == 0,
            "These are reached now; delete them from Unreached so the debt cannot grow back:\n  " + string.Join("\n  ", paid));
    }

    // ---- the route table ----------------------------------------------------------------------------------

    private sealed record Route(string Template, Regex Pattern, string[] Methods);

    private List<Route>? _routes;

    private List<Route> Routes() => _routes ??= factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .Select(e => (Template: e.RoutePattern.RawText?.Trim('/') ?? string.Empty,
            Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ToArray() ?? []))
        .Where(r => r.Template.StartsWith("api", StringComparison.Ordinal) && r.Methods.Length > 0)
        .Select(r => new Route(r.Template, ToRegex(r.Template), r.Methods))
        .ToList();

    private IEnumerable<string> Endpoints() =>
        Routes().SelectMany(r => r.Methods.Select(m => $"{m} {r.Template}")).Distinct(StringComparer.Ordinal);

    private static Regex ToRegex(string template)
    {
        var pattern = Regex.Replace(Regex.Escape(template), @"\\\{\*\*[^}]*\}", ".+");
        pattern = Regex.Replace(pattern, @"\\\{[^}]*\}", "[^/]+");
        return new Regex($"^{pattern}$", RegexOptions.CultureInvariant);
    }

    /// <summary>The route templates a concrete or templated href matches (the most specific first wins nothing: all count).</summary>
    private IEnumerable<Route> Match(string href)
    {
        var path = href.Split('?', '#')[0].Trim('/');
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath.Trim('/');
        }

        // An href that is itself a template ({id}) matches the route whose template it spells.
        var concrete = Regex.Replace(path, @"\{[^}]*\}", "x");
        return Routes().Where(r => r.Pattern.IsMatch(concrete));
    }

    // ---- the crawl ----------------------------------------------------------------------------------------

    // One crawl per test run, shared by both tests: it is the slow part, and both ask the same question of it.
    private static Task<(HashSet<string> Covered, int Requests)>? _crawl;

    private Task<(HashSet<string> Covered, int Requests)> CrawlAsync() => _crawl ??= CrawlOnceAsync();

    private async Task<(HashSet<string> Covered, int Requests)> CrawlOnceAsync()
    {
        var principals = await PrincipalsAsync();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var requests = 0;
        foreach (var client in principals)
        {
            requests += await CrawlAsAsync(client, covered);
            client.Dispose();
        }

        return (covered, requests);
    }

    private async Task<int> CrawlAsAsync(HttpClient client, HashSet<string> covered)
    {
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var perTemplate = new Dictionary<string, int>(StringComparer.Ordinal);
        queue.Enqueue("/api");
        seen.Add("/api");
        var requests = 0;

        while (queue.Count > 0 && requests < BudgetPerPrincipal)
        {
            var href = queue.Dequeue();
            requests++;
            using var response = await client.GetAsync(href.TrimStart('/'));
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            JsonElement body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<JsonElement>();
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var link in Links(body))
            {
                // An advertised ADDRESS covers every method its route serves there: several methods on one address
                // are one rel, and the method says which action (ADR 0719). A client creates a version by POSTing
                // to the `versions` it was given.
                foreach (var route in Match(link.Href))
                {
                    foreach (var method in route.Methods)
                    {
                        covered.Add($"{method} {route.Template}");
                    }
                }

                if (link.Method != "GET" || link.Href.Contains('{') || !seen.Add(link.Href))
                {
                    continue;
                }

                var template = Match(link.Href).Select(r => r.Template).FirstOrDefault() ?? link.Href;
                var n = perTemplate.GetValueOrDefault(template);
                if (n < InstancesPerTemplate)
                {
                    perTemplate[template] = n + 1;
                    queue.Enqueue(link.Href);
                }
            }
        }

        return requests;
    }

    /// <summary>Every <c>{ rel, href, method? }</c> object anywhere in a response — links ride on rows, actions, everything.</summary>
    private static IEnumerable<Link> Links(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("rel", out var rel) && rel.ValueKind == JsonValueKind.String)
                {
                    var method = element.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()!.ToUpperInvariant()
                        : "GET";
                    yield return new Link(href.GetString()!, method);
                }

                // An address the server hands out as a NAMED property is advertised too: a module action's commitHref
                // and optionsHref (ADR 0786), and any other *Href. Fetched only when it is an optionsHref-like GET.
                foreach (var property in element.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String
                    && p.Name.EndsWith("Href", StringComparison.Ordinal) && p.Name != "href"))
                {
                    yield return new Link(property.Value.GetString()!, property.Name == "commitHref" ? "POST" : "GET");
                }

                foreach (var property in element.EnumerateObject())
                {
                    foreach (var link in Links(property.Value))
                    {
                        yield return link;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var link in Links(item))
                    {
                        yield return link;
                    }
                }

                break;
        }
    }

    // ---- the principals and their archive -------------------------------------------------------------------

    private async Task<List<HttpClient>> PrincipalsAsync()
    {
        var (clientId, secret, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));

        var adminEmail = $"reach-admin-{Guid.NewGuid():N}@e2e.local";
        var userEmail = $"reach-user-{Guid.NewGuid():N}@e2e.local";
        const string password = "reach-1234";
        await factory.SeedUserAsync(tenantId, adminEmail, password, "Reach Admin", canViewAuditLog: true, canManageUsers: true,
            canResetMfa: true, canExport: true, canImport: true, canManageServiceAccounts: true, canManageRepositories: true,
            canManageIntrays: true, canCreateExternalLink: true, canManageMailRouting: true, canBlockResources: true,
            canReleaseResources: true, isTenantAdmin: true);
        await factory.GrantTenantAdminAsync(adminEmail);
        await factory.GrantCanLegalHoldAsync(adminEmail);
        var userId = await factory.SeedUserAsync(tenantId, userEmail, password, "Reach User");
        var (platformId, platformSecret) = await factory.SeedPlatformAdministratorAsync();

        var admin = factory.CreateAuthedClient(await factory.GetUserTokenAsync(adminEmail, password));
        var user = factory.CreateAuthedClient(await factory.GetUserTokenAsync(userEmail, password));
        await SeedArchiveAsync(owner, admin, user, userId);

        return [owner, admin, user, factory.CreateAuthedClient(await factory.GetTokenAsync(platformId, platformSecret))];
    }

    /// <summary>
    /// An archive with the shapes whose rels lead further: a repository with a folder, a versioned document checked out,
    /// annotated, tagged, held, linked externally, referenced and in review; a meeting room with a booking; notes,
    /// contacts and appointments in a personal space; an intray item; a group, a saved search, a label, a mail domain, a
    /// deactivated user and a deleted document. Built by composed URLs on purpose: this is setup, not the crawl.
    /// </summary>
    private async Task SeedArchiveAsync(HttpClient owner, HttpClient admin, HttpClient user, Guid userId)
    {
        var repo = (await TestJson.Post(owner, "/api/repositories", new { name = $"Reach {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folder = (await TestJson.Post(owner, $"/api/documents/{repo}/children", new { name = "Folder" })).GetProperty("id").GetGuid();
        var doc = await VersionedDocumentAsync(owner, folder, "Document");
        var reviewed = await VersionedDocumentAsync(owner, folder, "Reviewed");
        var deleted = await VersionedDocumentAsync(owner, folder, "Deleted");

        // The user may read the repository (an ACL entry, and a notification), and reviews one version.
        await TestJson.Put(owner, $"/api/documents/{repo}/acl-entries/users/{userId}", new { canSee = true, canReadContent = true });
        var reviewedVersion = (await TestJson.Get(owner, $"/api/documents/{reviewed}/versions")).GetProperty("versions")[0].GetProperty("id").GetGuid();
        await TestJson.Post(owner, $"/api/documents/{reviewed}/versions/{reviewedVersion}/workflow/submit", new { reviewerId = userId });

        var version = (await TestJson.Get(owner, $"/api/documents/{doc}/versions")).GetProperty("versions")[0].GetProperty("id").GetGuid();
        await TestJson.Post(owner, $"/api/documents/{doc}/versions/{version}/annotations",
            new { pageIndex = 0, kind = 0, positionX = 0.1, positionY = 0.1, width = 0.2, height = 0.06, text = "x", color = "#FFEB3B" });
        await TestJson.Put(owner, $"/api/documents/{doc}/tags", new { tags = new[] { "reach" } });
        await TestJson.Post(owner, $"/api/documents/{folder}/references", new { targetId = reviewed });

        // As the administrator: a tag, a label, a hold, an external link, a checkout, a mail domain, a group, a deleted
        // document, a deactivated user.
        await TestJson.Post(admin, "/api/tags", new { name = $"Reach {Guid.NewGuid():N}"[..14], color = "#FF0000" });
        await TestJson.Post(admin, "/api/sensitivity-labels", new { name = $"Reach {Guid.NewGuid():N}"[..14], rank = 9, color = "#000000", watermark = true });
        var hold = (await TestJson.Post(admin, "/api/legal-holds", new { name = "Matter", reason = "reach" })).GetProperty("id").GetGuid();
        await TestJson.Post(admin, $"/api/legal-holds/{hold}/items", new { documentId = doc });
        await TestJson.Put(admin, "/api/tenant-settings/external-links",
            new { allowExternalLinks = true, externalLinkMaxDays = 30, externalLinkDefaultAccesses = 2, showExternalLinkUrl = false });
        await TestJson.Post(admin, $"/api/documents/{doc}/external-links", new { });
        (await admin.PutAsync($"/api/documents/{doc}/checkout", null)).EnsureSuccessStatusCode();
        await TestJson.Post(admin, "/api/tenant/mail-domains", new { domain = $"reach-{Guid.NewGuid():N}"[..14] + ".example" });
        var group = (await TestJson.Post(admin, "/api/groups", new { name = $"Reach {Guid.NewGuid():N}"[..14] })).GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/groups/{group}/members", new { userId })).EnsureSuccessStatusCode();
        var leaver = (await TestJson.Post(admin, "/api/users",
            new { email = $"reach-leaver-{Guid.NewGuid():N}@e2e.local", displayName = "Leaver", password = "e2e-seeded-reader-1234" })).GetProperty("id").GetGuid();
        (await admin.DeleteAsync($"/api/users/{leaver}")).EnsureSuccessStatusCode();
        using (var head = new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{deleted}"))
        {
            var etag = (await admin.SendAsync(head)).Headers.ETag!;
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{deleted}");
            delete.Headers.IfMatch.Add(etag);
            (await admin.SendAsync(delete)).EnsureSuccessStatusCode();
        }

        // A meeting room with a booking.
        var rooms = (await TestJson.Get(owner, "/api/masks")).GetProperty("masks").EnumerateArray()
            .First(m => m.GetProperty("name").GetString() == "Meeting room").GetProperty("id").GetGuid();
        var room = (await TestJson.Post(owner, $"/api/documents/{repo}/children", new { name = "Room 1", maskId = rooms })).GetProperty("id").GetGuid();
        await TestJson.Post(owner, $"/api/documents/{room}/bookings",
            new { startsAt = "2027-03-10T10:00:00Z", endsAt = "2027-03-10T12:00:00Z", purpose = "Reach" });

        // As the user: a saved search shared with nobody but kept, notes, a contact, an appointment, an intray item.
        await TestJson.Post(user, "/api/saved-searches", new { name = "Reach", queryString = "q=reach", shareScope = 0 });
        var personal = (await TestJson.Post(user, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        await TestJson.Post(user, "/api/me/imap-access", new { });
        var spaces = (await TestJson.Get(user, $"/api/documents/{personal}/children")).GetProperty("children").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetGuid());
        var notebook = (await TestJson.Post(user, $"/api/documents/{spaces["My Mailbox"]}/children", new { name = "Notebook", folderMask = "notes" })).GetProperty("id").GetGuid();
        var section = (await TestJson.Post(user, $"/api/documents/{notebook}/sections", new { name = "Work" })).GetProperty("id").GetGuid();
        await TestJson.Post(user, $"/api/documents/{section}/notes", new { title = "Note", body = "reach" });
        await TestJson.Post(user, $"/api/documents/{spaces["My Addressbook"]}/contacts",
            new { formattedName = "Ada Lovelace", emails = new[] { new { value = "ada@example.com", type = "work" } } });
        await TestJson.Post(user, $"/api/documents/{spaces["My Calendar"]}/appointments",
            new { summary = "Reach", start = "2027-03-11T09:00:00Z", end = "2027-03-11T10:00:00Z", location = "Here" });
        var intray = await TestJson.Post(user, "/api/intray", new { fileName = "scan.txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(intray.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("reach")))).EnsureSuccessStatusCode();
        }
    }

    private static async Task<Guid> VersionedDocumentAsync(HttpClient owner, Guid parent, string name)
    {
        var doc = (await TestJson.Post(owner, $"/api/documents/{parent}/children", new { name })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(owner, $"/api/documents/{doc}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(name)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{doc}/versions/{version.GetProperty("id").GetGuid()}", new { });
        return doc;
    }
}
