using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// Copying a repository document into the intray as a TEMPLATE (#467): the bytes and the document's mask + index
// values arrive together, so new work can start from an existing document without creating a document or a
// version until it is filed.
//
// Tested at the API rather than only through the browser because the interesting part is server-side — an object
// copy and a sidecar write in one request — and because the UI test can only observe the result indirectly (an
// item that is not square-bracketed).
[Collection(E2ECollection.Name)]
public class IntrayFromDocumentTests
{
    private readonly E2EApiFactory _factory;

    public IntrayFromDocumentTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_document_is_copied_into_the_intray_with_its_mask_and_index_values()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"owner-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Owner", canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var (maskId, fieldId) = await _factory.SeedMaskWithSelectFieldAsync(tenantId, "Vendor");
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Templates {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var docName = $"template-{Guid.NewGuid():N}";
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = docName })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("the template body")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/index-data",
            new { fields = new[] { new { fieldDefinitionId = fieldId, fieldName = "Vendor", values = new[] { "Acme" } } } })).EnsureSuccessStatusCode();

        // The action is reached by FOLLOWING the rel the intray advertises, never by composing the path — the
        // same way a conforming client must (ADR 0543).
        var intray = await TestJson.Get(owner, "/api/intray");
        var fromDocument = intray.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "from-document").GetProperty("href").GetString()!;

        (await owner.PostAsJsonAsync(fromDocument, new { documentId = docId })).EnsureSuccessStatusCode();

        // It lands in the intray under the document's name plus the version's extension. That naming is not
        // incidental: it is what lets the item later be dragged back onto Check-out and matched by filename.
        var itemName = docName + ".txt";
        var items = (await TestJson.Get(owner, "/api/intray")).GetProperty("items").EnumerateArray().ToList();
        var item = items.FirstOrDefault(i => i.GetProperty("name").GetString() == itemName);
        Assert.True(item.ValueKind is not System.Text.Json.JsonValueKind.Undefined, $"'{itemName}' is not in the intray.");

        // hasMask is what the UI renders as "not square-bracketed" — the staged draft exists.
        Assert.True(item.GetProperty("hasMask").GetBoolean());

        // …and the draft carries the SOURCE's mask and values, which is the whole point of a template. A copy
        // that arrived with the bytes but no index data would look identical in a listing.
        var draft = await TestJson.Get(owner, $"/api/intray/{itemName}/mask");
        Assert.Equal(maskId, draft.GetProperty("maskId").GetGuid());
        Assert.Equal(docName, draft.GetProperty("name").GetString());
        var values = draft.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("fieldDefinitionId").GetGuid() == fieldId)
            .GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["Acme"], values);

        // A second copy of the same document conflicts rather than overwriting: the intray is addressed BY NAME,
        // so a silent overwrite would destroy the first item's staged edits.
        var second = await owner.PostAsJsonAsync(fromDocument, new { documentId = docId });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_folder_has_nothing_to_copy_and_an_unreadable_document_is_refused()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var ownerEmail = $"owner-{Guid.NewGuid():N}@e2e.local";
        var strangerEmail = $"stranger-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, ownerEmail, "u-1234", "Owner", canManageRepositories: true);
        await _factory.SeedUserAsync(tenantId, strangerEmail, "u-1234", "Stranger");
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(ownerEmail, "u-1234"));
        using var stranger = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(strangerEmail, "u-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Templates {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"folder-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // A folder has no version to copy. It is a CONFLICT, not a 500 — the caller asked for something
        // coherent that simply does not apply to this document.
        var folder = await owner.PostAsJsonAsync("/api/intray/from-document", new { documentId = folderId });
        Assert.Equal(HttpStatusCode.Conflict, folder.StatusCode);

        // Reading the source is the only right required — and someone who cannot read it gets nothing. Without
        // this, the endpoint would be a way to copy any document in the tenant into your own intray.
        var refused = await stranger.PostAsJsonAsync("/api/intray/from-document", new { documentId = folderId });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // A document that does not exist is a 404, not a 403 — the caller is not being told about something
        // they may not see, because there is nothing there.
        var missing = await owner.PostAsJsonAsync("/api/intray/from-document", new { documentId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
