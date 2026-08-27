using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace SimplArchive.EndToEndTests;

// A WebDAV client navigates by HREF, not by displayname — the hrefs a PROPFIND returns ARE the tree it draws.
// `WebDavTreeParityTests` matches `<D:displayname>` only, so it can (and does) pass while every href points
// somewhere else: it proves the right NAMES are listed, never that they are listed in the right PLACE.
[Collection(E2ECollection.Name)]
public partial class WebDavHrefTests
{
    private const string WebDavMountPath = "/SimplArchive";

    // The personal space is named after its owner (ADR 0671), so its WebDAV/IMAP path segment is
    // whatever this test seeded as the display name — not the constant "Personal" it used to be.
    private const string Personal = "Href User";

    // An href is a URL, so the segment is percent-encoded — "Href User" arrives as "Href%20User". That never
    // showed while every personal space was called "Personal": a one-word name has nothing to escape. Naming
    // them after people (ADR 0671) makes a space the NORMAL case, so the expectation has to be built the same
    // way the server builds the href rather than from the display name directly.
    private static readonly string PersonalSegment = Uri.EscapeDataString(Personal);

    private readonly E2EApiFactory _factory;

    public WebDavHrefTests(E2EApiFactory factory) => _factory = factory;

    /// <summary>
    /// Every member href a PROPFIND returns must sit under the collection that was asked for.
    /// </summary>
    /// <remarks>
    /// RFC 4918 §9.1: a Depth-1 PROPFIND answers with the collection and its members. A member href outside the
    /// requested collection is not a member — and a client handed one has to guess. That is what makes the
    /// mounted drive draw the tree wrongly, with the repositories appearing to hang UNDER the mount's own root
    /// rather than beside it.
    ///
    /// This ran for BOTH prefixes while the legacy `/webdav` alias existed, because that is where the two could
    /// disagree — the gateway answered on one path and composed its hrefs from a constant. With the alias
    /// retired there is one mount, so the theory's second case would be the first one repeated (#794).
    /// </remarks>
    [Fact]
    public async Task Every_member_href_sits_under_the_collection_that_was_asked_for()
    {
        const string mount = WebDavMountPath;
        var (dav, basic, repository) = await MountAsync();
        using var _d = dav;

        var hrefs = await PropFindHrefsAsync(dav, basic, mount);

        // The collection's own href comes first and must be the mount itself — a client compares this against
        // what it requested to know which response row describes the collection rather than a member.
        Assert.Equal($"{mount}/", hrefs[0]);

        // …and the members are the top level of the Repositories tree (ADR 0509), each one directly beneath.
        Assert.Contains($"{mount}/{PersonalSegment}/", hrefs);
        Assert.Contains($"{mount}/{repository}/", hrefs);

        Assert.All(hrefs, href => Assert.StartsWith($"{mount}/", href, StringComparison.Ordinal));
    }

    /// <summary>
    /// The mount's own children are siblings — neither the personal space nor a repository contains the other.
    /// </summary>
    /// <remarks>
    /// The reported symptom was the two appearing "cascaded" on the drive. Asserted on the DEPTH of the href
    /// rather than on its text: `/SimplArchive/{Personal}/Demo/` would satisfy a `Contains("Personal")` check and
    /// still be exactly the bug.
    /// </remarks>
    [Fact]
    public async Task The_personal_space_and_a_repository_are_siblings_not_nested()
    {
        const string mount = WebDavMountPath;
        var (dav, basic, repository) = await MountAsync();
        using var _d = dav;

        var members = (await PropFindHrefsAsync(dav, basic, mount)).Skip(1).ToList();

        Assert.All(members, href =>
        {
            var relative = href[$"{mount}/".Length..].TrimEnd('/');
            Assert.False(
                relative.Contains('/', StringComparison.Ordinal),
                $"'{href}' is nested under another member; the mount's children must be one segment deep.");
        });

        Assert.Contains(members, h => h.EndsWith($"/{PersonalSegment}/", StringComparison.Ordinal));
        Assert.Contains(members, h => h.EndsWith($"/{repository}/", StringComparison.Ordinal));
    }

    /// <summary>A user with a DAV password, a repository to see, and a provisioned personal space.</summary>
    private async Task<(HttpClient Dav, AuthenticationHeaderValue Basic, string Repository)> MountAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repository = $"Demo{Guid.NewGuid():N}"[..12];
        await TestJson.Post(admin, "/api/repositories", new { name = repository });

        var email = $"href-{Guid.NewGuid():N}@e2e.local";
        const string password = "href-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Href User", isTenantAdmin: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // The personal space is provisioned on demand — the WebDAV root lists it, so it must exist first.
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return (_factory.CreateClient(), basic, repository);
    }

    private static async Task<List<string>> PropFindHrefsAsync(
        HttpClient dav, AuthenticationHeaderValue basic, string path, string depth = "1")
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = basic } };
        request.Headers.TryAddWithoutValidation("Depth", depth);
        using var response = await dav.SendAsync(request);
        var xml = await response.Content.ReadAsStringAsync();
        return [.. HrefRegex().Matches(xml).Select(m => m.Groups[1].Value)];
    }

    [GeneratedRegex("<D:href>([^<]*)</D:href>")]
    private static partial Regex HrefRegex();
}
