using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SimplArchive.EndToEndTests;

// PROPFIND answers the question it was asked (issue #801, RFC 4918 §9.1). The gateway used to ignore the
// request body entirely: whatever the client asked for, every 207 carried the same fixed property set in a
// single 200 propstat, and a property we lack was answered with SILENCE — indistinguishable from a truncated
// answer. Now the requested-and-known properties come back in the 200 propstat, the requested-and-unknown ones
// in a 404 propstat NAMING them, and <D:propname/> lists the names. Asserted over the real HTTP surface against
// BOTH composers — a tree node and a special folder — because the filter lives at the one choke point every
// multistatus passes through, and this is the proof that both actually pass through it. The current behaviour
// was invisible to status-code tests, which is why it survived #794 (the issue's own words).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class WebDavRequestedPropertyTests
{
    private const string Personal = "Req User";

    private static readonly XNamespace Dav = "DAV:";

    private readonly E2EApiFactory _factory;

    public WebDavRequestedPropertyTests(E2EApiFactory factory) => _factory = factory;

    /// <summary>A tree node answers with the requested properties, and 404-names the one we lack.</summary>
    [Fact]
    public async Task A_tree_folder_partitions_the_requested_set_into_200_and_404_propstats()
    {
        var ctx = await SetupAsync();

        var document = await PropFindAsync(ctx, $"/SimplArchive/{Personal}",
            Body("<D:prop><D:displayname/><D:quota-available-bytes/></D:prop>"));

        var ok = Propstat(document, "200");
        Assert.Equal(Personal, ok.Descendants(Dav + "displayname").Single().Value);
        Assert.Empty(ok.Descendants(Dav + "getlastmodified")); // composed but unrequested — stays out

        Assert.NotEmpty(Propstat(document, "404").Descendants(Dav + "quota-available-bytes"));
    }

    /// <summary>A special-folder file passes through the same filter — one contract, not one per emitter.</summary>
    [Fact]
    public async Task An_intray_item_partitions_the_requested_set_the_same_way()
    {
        var ctx = await SetupAsync();
        var item = $"req{Guid.NewGuid().ToString("N")[..8]}.docx";
        var path = $"/SimplArchive/{Personal}/Intray/{item}";
        (await DavAsync(ctx, "PUT", path, new ByteArrayContent(Encoding.UTF8.GetBytes("staged")))).EnsureSuccessStatusCode();

        var document = await PropFindAsync(ctx, path, Body("<D:prop><D:getetag/><D:quota-available-bytes/></D:prop>"));

        var ok = Propstat(document, "200");
        Assert.False(string.IsNullOrWhiteSpace(ok.Descendants(Dav + "getetag").Single().Value));
        Assert.Empty(ok.Descendants(Dav + "displayname"));

        Assert.NotEmpty(Propstat(document, "404").Descendants(Dav + "quota-available-bytes"));
    }

    /// <summary>&lt;D:propname/&gt; lists what the server has, without the values (RFC 4918 §9.1).</summary>
    [Fact]
    public async Task Propname_lists_the_property_names_without_values()
    {
        var ctx = await SetupAsync();

        var document = await PropFindAsync(ctx, $"/SimplArchive/{Personal}", Body("<D:propname/>"));

        var prop = Propstat(document, "200").Descendants(Dav + "prop").Single();
        Assert.Contains(prop.Elements(), e => e.Name == Dav + "getlastmodified");
        Assert.All(prop.Elements(), e => Assert.Empty(e.Nodes()));
    }

    /// <summary>No body stays allprop — the fixed set every OS mount was measured happy with (#794).</summary>
    [Fact]
    public async Task An_empty_body_still_answers_the_full_property_set()
    {
        var ctx = await SetupAsync();

        var document = await PropFindAsync(ctx, $"/SimplArchive/{Personal}", body: null);

        var prop = Propstat(document, "200").Descendants(Dav + "prop").Single();
        foreach (var expected in new[] { "displayname", "resourcetype", "getlastmodified", "creationdate", "supportedlock" })
        {
            Assert.Contains(prop.Elements(), e => e.Name == Dav + expected);
        }
    }

    private static StringContent Body(string inner) => new(
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\">{inner}</D:propfind>",
        Encoding.UTF8, "text/xml");

    private static XElement Propstat(XDocument document, string status)
    {
        var match = document.Descendants(Dav + "propstat")
            .SingleOrDefault(p => p.Element(Dav + "status")!.Value.Contains(status));
        Assert.True(match is not null, $"no propstat with status {status} in: {document}");
        return match!;
    }

    private async Task<XDocument> PropFindAsync(Context ctx, string path, StringContent? body)
    {
        var response = await DavAsync(ctx, "PROPFIND", path, body, ("Depth", "0"));
        Assert.True(response.IsSuccessStatusCode, $"PROPFIND {path} returned {(int)response.StatusCode}");
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private sealed record Context(AuthenticationHeaderValue Basic, HttpClient Dav);

    private async Task<Context> SetupAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var email = $"req-{Guid.NewGuid():N}@e2e.local";
        const string password = "req-1234";
        await _factory.SeedUserAsync(tenantId, email, password, Personal);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        return new Context(basic, _factory.CreateClient());
    }

    private static Task<HttpResponseMessage> DavAsync(Context ctx, string method, string path, HttpContent? body = null, params (string Key, string Value)[] headers)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = ctx.Basic } };
        if (body is not null)
        {
            request.Content = body;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return ctx.Dav.SendAsync(request);
    }
}
