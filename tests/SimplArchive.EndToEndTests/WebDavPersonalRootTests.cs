using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The personal space's first level is closed (#634, ADR 0636), and WebDAV is the surface that had no
// affordance to hide — ADR 0636 said so about `MKCOL`, which is refused. `PUT` was not covered.
//
// It was reachable: `WebDavMiddleware` created the document MASKLESS, and maskless is admitted at that level
// because it is the pre-upgrade state. `DocumentFinalizer` then stamped Basic Entry once the bytes arrived,
// and the first-level rule — gated on ARRIVAL — never looked again. So dragging a file onto the mounted
// `Personal` drive filed it exactly where nothing else in the product allows (#644).
//
// Both halves are asserted here, because either alone would leave the door open: the create now stamps a mask
// (so it is refused BEFORE any bytes transfer, as the API and both clients already do), and the invariant now
// re-checks on mask assignment (so a future path that creates maskless and masks later cannot walk past it).
[Collection(E2ECollection.Name)]
public class WebDavPersonalRootTests
{
    // The personal space is named after its owner (ADR 0671), so its WebDAV/IMAP path segment is
    // whatever this test seeded as the display name — not the constant "Personal" it used to be.
    private const string Personal = "Dav Root";

    private readonly E2EApiFactory _factory;

    public WebDavPersonalRootTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_file_dropped_on_Personal_is_refused_while_My_Documents_takes_it()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var email = $"davroot-{Guid.NewGuid():N}@e2e.local";
        const string password = "davroot-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav Root");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        async Task<HttpResponseMessage> PutAsync(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, path) { Headers = { Authorization = basic } };
            req.Content = new ByteArrayContent("some bytes"u8.ToArray());
            return await dav.SendAsync(req);
        }

        // The personal space's first level holds only what it was provisioned with. Refused — and refused at
        // CREATION, which is why the status is a conflict rather than a late failure after the transfer.
        var refused = await PutAsync($"/SimplArchive/{Personal}/dropped-on-personal.txt");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // …while the folder that exists for exactly this takes it. Asserted alongside, because a middleware
        // that refused every PUT would satisfy the assertion above and break the mounted drive entirely.
        var accepted = await PutAsync($"/SimplArchive/{Personal}/My Documents/dropped-in-my-documents.txt");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }
}
