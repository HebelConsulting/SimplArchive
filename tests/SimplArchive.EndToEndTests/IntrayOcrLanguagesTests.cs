using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end: staging OCR languages on a scannable intray item (ADR "Inbox OCR-language staging") is carried by
// the {name}.mask.json sidecar and applied to the filed version's OcrLanguages at filing (before the
// searchable-PDF conversion runs).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class IntrayOcrLanguagesTests
{
    private readonly E2EApiFactory _factory;

    public IntrayOcrLanguagesTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Staged_ocr_languages_are_applied_to_the_filed_version()
    {
        // A real logged-in User (the intray is scoped to the token's userId).
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"intrayocr-{Guid.NewGuid():N}@e2e.local";
        const string password = "intrayocr1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Intray OCR User");
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // File into the user's own personal repository (full rights, no ACL setup needed).
        var repoId = (await TestJson.Post(user, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();

        // Upload a scannable item + stage OCR languages (German first, then English) on its sidecar.
        const string name = "scan.tif";
        var upload = await TestJson.Post(user, "/api/intray", new { fileName = name });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("scan-bytes")))).EnsureSuccessStatusCode();
        }
        (await user.PutAsJsonAsync($"/api/intray/{name}/mask", new { ocrLanguages = new[] { "deu", "eng" } })).EnsureSuccessStatusCode();

        // File it → the filed version carries the staged, ordered OCR languages.
        var filed = await TestJson.Post(user, $"/api/intray/{name}/file", new { folderId = repoId });
        var docId = filed.GetProperty("id").GetGuid();

        var versions = await TestJson.Get(user, $"/api/documents/{docId}/versions");
        var v1 = versions.GetProperty("versions").EnumerateArray().First(v => v.GetProperty("versionNumber").GetInt32() == 1);
        Assert.Equal("deu+eng", v1.GetProperty("ocrLanguages").GetString());
    }
}
