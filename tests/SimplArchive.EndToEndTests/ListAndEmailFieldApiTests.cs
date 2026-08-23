using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// Lists as an orthogonal property of a field, and the EmailAddress type, over the wire (#703).
//
// The unit and integration tests cover the editor and the validator; what only a wire test can show is that
// the flag SURVIVES the round trip a client actually makes — declared on POST /api/masks, read back on GET,
// and then honoured by the multiplicity check on PUT /index-data. A flag lost at any of those three points
// leaves every other assertion green while a list field behaves as single-valued.
[Collection(E2ECollection.Name)]
public class ListAndEmailFieldApiTests
{
    private readonly E2EApiFactory _factory;

    public ListAndEmailFieldApiTests(E2EApiFactory factory) => _factory = factory;

    private async Task<HttpClient> MaskManagerAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"lists-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "lists-1234", "Lists", canManageRepositories: true);
        await _factory.GrantTenantAdminAsync(email); // carries CanManageMasks
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "lists-1234"));
    }

    /// <summary>A mask with one list field and one single-valued field of the same type, plus a document on it.</summary>
    private static async Task<(Guid RepositoryId, Guid DocumentId, Guid ListFieldId, Guid SingleFieldId, string MaskName)> OnAMaskAsync(
        HttpClient api, FieldDataType type)
    {
        // The create endpoint takes the data type as its ENUM ORDINAL while the read returns its name — a
        // pre-existing asymmetry, and the reason this casts rather than sending "EmailAddress" as a string.
        var dataType = (int)type;
        var maskName = $"Claims {Guid.NewGuid():N}";
        var mask = await TestJson.Post(api, "/api/masks", new
        {
            name = maskName,
            fields = new object[]
            {
                new { name = "Many", dataType, isRequired = false, isList = true },
                new { name = "One", dataType, isRequired = false, isList = false },
            },
        });

        var fields = mask.GetProperty("fields").EnumerateArray().ToList();
        var many = fields.Single(f => f.GetProperty("name").GetString() == "Many");
        var one = fields.Single(f => f.GetProperty("name").GetString() == "One");

        // Read back through GET, not merely trusted from the create response: the two build the resource by
        // different routes, and it is the READ that every client depends on.
        var reread = await TestJson.Get(api, $"/api/masks/{mask.GetProperty("id").GetGuid()}");
        var rereadFields = reread.GetProperty("fields").EnumerateArray().ToList();
        Assert.True(rereadFields.Single(f => f.GetProperty("name").GetString() == "Many").GetProperty("isList").GetBoolean());
        Assert.False(rereadFields.Single(f => f.GetProperty("name").GetString() == "One").GetProperty("isList").GetBoolean());

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Lists {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // A real confirmed version, because an export includes only documents that HAVE one (plus their
        // ancestors) — a versionless document exports as nothing, and with it goes the mask that was the point
        // of the archive test. Uploaded BEFORE the mask is assigned: auto-classification stamps its own mask at
        // finalize, so doing it the other way round would quietly overwrite the mask under test.
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"lists-{Guid.NewGuid():N}")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(api, $"/api/documents/{docId}/mask", new { maskId = mask.GetProperty("id").GetGuid() });

        return (repoId, docId, many.GetProperty("id").GetGuid(), one.GetProperty("id").GetGuid(), maskName);
    }

    // Copied rather than shared, as the sibling import tests do: it is three lines of shaping a request.
    private static MultipartFormDataContent MultipartOf(byte[] zip)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "import.zip");
        return content;
    }

    private static object IndexData(Guid fieldId, params string[] values) =>
        new { fields = new[] { new { fieldDefinitionId = fieldId, values } } };

    private static async Task<List<string>> ValuesAsync(HttpClient api, Guid documentId, string fieldName) =>
        (await TestJson.Get(api, $"/api/documents/{documentId}/index-data"))
            .GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldName").GetString() == fieldName)
            .GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToList();

    [Fact]
    public async Task A_list_field_accepts_many_values_and_the_same_type_without_the_flag_does_not()
    {
        using var api = await MaskManagerAsync();
        var (_, docId, many, one, _) = await OnAMaskAsync(api, FieldDataType.Text);

        await TestJson.Put(api, $"/api/documents/{docId}/index-data", IndexData(many, "alpha", "beta", "gamma"));
        Assert.Equal(["alpha", "beta", "gamma"], await ValuesAsync(api, docId, "Many"));

        // The order is the CALLER's, not the database's, and it is the same on every read. This assertion is
        // here because it caught the opposite: rows carry no inherent order, so before ordinals the values
        // came back rearranged — differently between runs, which is a user watching their own typing shuffle.
        for (var read = 0; read < 3; read++)
        {
            Assert.Equal(["alpha", "beta", "gamma"], await ValuesAsync(api, docId, "Many"));
        }

        // …and a REORDER is a real edit: the same three values in a different order must come back that way,
        // which an implementation that merely sorted the values would fail.
        await TestJson.Put(api, $"/api/documents/{docId}/index-data", IndexData(many, "gamma", "alpha", "beta"));
        Assert.Equal(["gamma", "alpha", "beta"], await ValuesAsync(api, docId, "Many"));

        // The pair is the assertion. Both fields are the same TYPE, so a refusal here can only come from the
        // flag — which is what "orthogonal to type" has to mean in practice.
        var refused = await api.PutAsJsonAsync($"/api/documents/{docId}/index-data", IndexData(one, "alpha", "beta"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("MULTIPLE_VALUES_NOT_ALLOWED",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_email_list_refuses_a_malformed_element_and_keeps_none_of_them()
    {
        using var api = await MaskManagerAsync();
        var (_, docId, many, _, _) = await OnAMaskAsync(api, FieldDataType.EmailAddress);

        await TestJson.Put(api, $"/api/documents/{docId}/index-data", IndexData(many, "events@demo.dev"));

        // Good, BAD, good — a validator that checked only the first element would pass this with the bad one
        // stored. The whole PUT is one save, so the refusal must leave the previous value untouched.
        var refused = await api.PutAsJsonAsync($"/api/documents/{docId}/index-data",
            IndexData(many, "sales@demo.dev", "not-an-address", "hr@demo.dev"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("FIELD_VALUE_INVALID",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal(["events@demo.dev"], await ValuesAsync(api, docId, "Many"));
    }

    [Fact]
    public async Task An_export_and_re_import_preserves_the_flag()
    {
        // The archive carries `isList` additively and FormatVersion deliberately stays 2, so an archive
        // written before #703 still imports — with every field single-valued, which is what they were. What
        // this asserts is the other half: a NEW archive must not lose the flag on the way back in, or an
        // imported mask silently becomes single-valued and the API starts refusing its second value.
        using var api = await MaskManagerAsync();
        var (repoId, docId, many, _, maskName) = await OnAMaskAsync(api, FieldDataType.EmailAddress);
        await TestJson.Put(api, $"/api/documents/{docId}/index-data", IndexData(many, "a@x.dev", "b@x.dev", "c@x.dev"));

        var zip = await (await api.GetAsync($"/api/documents/{repoId}/export?versions=all")).Content.ReadAsByteArrayAsync();

        Guid importedMaskId;
        Guid importedRoot;
        using (var content = MultipartOf(zip))
        {
            var response = await api.PostAsync("/api/repositories/import", content);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            importedRoot = (await TestJson.Read(response)).GetProperty("rootId").GetGuid();

            // A CUSTOM mask is re-created on import (a well-known one is merged into the destination's own),
            // so the archive's copy of the flag is what the new field definitions are built from. The new
            // mask wears a de-duplicated name, which is how it is told apart from the source it came from.
            var masks = (await TestJson.Get(api, "/api/masks")).GetProperty("masks").EnumerateArray()
                .Where(m => m.GetProperty("name").GetString()!.StartsWith(maskName, StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, masks.Count);
            importedMaskId = masks.Single(m => m.GetProperty("name").GetString() != maskName).GetProperty("id").GetGuid();
        }

        var fields = (await TestJson.Get(api, $"/api/masks/{importedMaskId}")).GetProperty("fields").EnumerateArray().ToList();
        Assert.True(fields.Single(f => f.GetProperty("name").GetString() == "Many").GetProperty("isList").GetBoolean());
        Assert.False(fields.Single(f => f.GetProperty("name").GetString() == "One").GetProperty("isList").GetBoolean());

        // The values' ORDER crosses the archive too. Without the ordinal riding along, a re-imported list
        // would arrive shuffled — the flag preserved and the content quietly rearranged.
        var importedDoc = (await TestJson.Get(api, $"/api/documents/{importedRoot}/children"))
            .GetProperty("children").EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(["a@x.dev", "b@x.dev", "c@x.dev"], await ValuesAsync(api, importedDoc, "Many"));
    }
}
