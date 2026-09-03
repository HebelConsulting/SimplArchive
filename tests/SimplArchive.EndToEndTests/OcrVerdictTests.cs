using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The persisted OCR verdict + the widened OCR-languages gate (#999), over the wire. This suite runs WITHOUT
// the OCR sidecar (Ocr unset → null queue, no worker), which is exactly what makes it the right place for
// the halves that must not depend on one: the finalizer's TIFF stamp, the rel emission, and the PUT that
// used to refuse every non-TIFF document. The worker's halves — PDF verdicts, successors, force — live in
// the desktop suite, whose stack runs the real sidecar.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class OcrVerdictTests
{
    private readonly E2EApiFactory _factory;

    public OcrVerdictTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, Guid DocId)> FiledAsync(string extension, byte[] bytes)
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Ocr {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var v = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = extension });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }
        (await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return (api, docId);
    }

    [Fact]
    public async Task A_tiff_version_is_stamped_convertible_at_finalize_and_offers_make_searchable()
    {
        var (api, docId) = await FiledAsync(".tif", Encoding.UTF8.GetBytes("not-a-real-tiff — the stamp is by extension, the worker never runs here"));

        var version = (await TestJson.Get(api, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray().Single();
        Assert.Equal("ConvertibleScan", version.GetProperty("ocrVerdict").GetString());
        Assert.Contains(version.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "make-searchable");

        // Following the rel answers 202 — the conversion is the worker's job, off the request path.
        var rel = version.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "make-searchable");
        var forced = await api.PostAsync(rel.GetProperty("href").GetString(), null);
        Assert.Equal(HttpStatusCode.Accepted, forced.StatusCode);

        api.Dispose();
    }

    [Fact]
    public async Task A_pdf_document_accepts_ocr_languages_where_it_used_to_answer_NO_TIFF_VERSION()
    {
        var (api, docId) = await FiledAsync(".pdf", SelfHosting.ImagePdf.ImageOnly());

        // The regression #999 names: the PUT targeted "the latest confirmed TIFF version" and refused every
        // PDF-sourced document — the server half of the selector never working for the very documents OCR
        // exists for.
        var response = await api.PutAsJsonAsync($"/api/documents/{docId}/ocr-languages", new { languages = new[] { "deu", "eng" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var version = (await TestJson.Get(api, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray().Single();
        Assert.Equal("deu+eng", version.GetProperty("ocrLanguages").GetString());
        // No sidecar in this suite → no worker → the PDF stays unjudged; null renders as no verdict line.
        Assert.Equal(JsonValueKind.Null, version.GetProperty("ocrVerdict").ValueKind);

        api.Dispose();
    }
}
