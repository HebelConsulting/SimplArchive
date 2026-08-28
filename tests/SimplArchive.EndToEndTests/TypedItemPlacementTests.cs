using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Where a .vcf/.ics may be filed, and what happens when containment refuses something (#665).
//
// The reported bug: dragging a contact card onto an ordinary folder — filing a card somebody e-mailed you —
// answered a bare 500. The classifier stamped `Contact` regardless of destination, which created the very
// containment violation that then refused the save, and nothing translated the refusal.
//
// Both halves are covered, because fixing either alone leaves a bad outcome: classify-by-destination without
// the translation still 500s on any OTHER containment refusal, and the translation without the classifier fix
// turns an ordinary drag into a legible failure rather than a success.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TypedItemPlacementTests
{
    private readonly E2EApiFactory _factory;

    public TypedItemPlacementTests(E2EApiFactory factory) => _factory = factory;

    private const string Card =
        "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:665-test\r\nFN:Robin Example\r\nN:Example;Robin;;;\r\nEND:VCARD\r\n";

    private static async Task<Guid> UploadAsync(HttpClient api, Guid folderId, string name, string extension, string content)
    {
        var doc = await TestJson.Post(api, $"/api/documents/{folderId}/children", new { name });
        var docId = doc.GetProperty("id").GetGuid();

        var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = extension });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        // The finalize that used to answer 500.
        (await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { }))
            .EnsureSuccessStatusCode();

        return docId;
    }

    [Fact]
    public async Task A_contact_card_filed_in_an_ordinary_folder_is_an_ordinary_document()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(api, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var docId = await UploadAsync(api, repo, $"card{Guid.NewGuid():N}"[..9], ".vcf", Card);

        // It is filed, and it is NOT a Contact — a card outside an addressbook is just a file, which is what it
        // is when nothing is going to sync it.
        var mask = await TestJson.Get(api, $"/api/documents/{docId}/mask");
        Assert.NotEqual("Contact", mask.GetProperty("name").GetString());
    }

    // The other side of the same rule: inside an addressbook the card IS a Contact, indistinguishable from one
    // a phone synced there. Classifying by destination must not have cost that.
    [Fact]
    public async Task A_contact_card_filed_in_an_addressbook_is_still_a_contact()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(api, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var book = (await TestJson.Post(api, $"/api/documents/{repo}/children",
            new { name = $"ab{Guid.NewGuid():N}"[..9], folderMask = "addressbook" })).GetProperty("id").GetGuid();

        var docId = await UploadAsync(api, book, $"card{Guid.NewGuid():N}"[..9], ".vcf", Card);

        var mask = await TestJson.Get(api, $"/api/documents/{docId}/mask");
        Assert.Equal("Contact", mask.GetProperty("name").GetString());
    }

    // The guard on the CLASS of failure, not on the one instance. Containment still refuses things — it must,
    // it is what makes an addressbook an addressbook — and every refusal has to arrive as a legible 4xx.
    //
    // Moving a real Contact OUT of its addressbook is the stable way to trigger one: it stays a violation
    // whatever the classifier does, so this keeps biting after #665 and does not depend on the bug it was
    // written for.
    [Fact]
    public async Task Containment_refusals_arrive_as_a_client_error_never_a_500()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repo = (await TestJson.Post(api, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var book = (await TestJson.Post(api, $"/api/documents/{repo}/children",
            new { name = $"ab{Guid.NewGuid():N}"[..9], folderMask = "addressbook" })).GetProperty("id").GetGuid();
        var plain = (await TestJson.Post(api, $"/api/documents/{repo}/children", new { name = $"pf{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();

        var contactId = await UploadAsync(api, book, $"card{Guid.NewGuid():N}"[..9], ".vcf", Card);

        var etag = (await api.GetAsync($"/api/documents/{contactId}")).Headers.ETag!.Tag;
        var move = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{contactId}/parent")
        {
            Content = JsonContent.Create(new { parentId = plain }),
        };
        move.Headers.TryAddWithoutValidation("If-Match", etag);
        var response = await api.SendAsync(move);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True((int)response.StatusCode is >= 400 and < 500,
            $"a containment refusal must be a client error, got {(int)response.StatusCode}");

        // ...and it must say WHY. A 4xx whose body names no cause is the same failure one status code up: the
        // user still cannot tell that the LOCATION is the problem.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Addressbook", body, StringComparison.OrdinalIgnoreCase);
    }
}
