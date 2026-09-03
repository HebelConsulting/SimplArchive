using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.SelfHosting;

namespace SimplArchive.UiEndToEndTests;

// The searchable-PDF pipeline against the REAL sidecar (#999): the whole circuit — filing, detection, the
// persisted verdict, the successor version, and the user's force override. This is the repro the issue
// asked for: an image-only PDF filed DIRECTLY (never through the inbox) must end searchable.
//
// This class owns its OWN SelfHostedApp with WithOcrSidecar = true — the suite's shared fixture runs
// WITHOUT the sidecar (a discovery of this issue: only the manual-capture harness enabled it), and
// enabling it fixture-wide would let asynchronous successors appear under every other test's feet. It
// stays in the collection so it SERIALIZES with the shared-fixture classes — two stacks at once is a
// machine symptom factory — while deliberately not taking the fixture.
[Collection(UiCollection.Name)]
public class DesktopOcrPipelineTests : IAsyncLifetime
{
    private readonly SelfHostedApp _app = new() { WithOcrSidecar = true };

    public Task InitializeAsync() => _app.StartAsync();

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<(HttpClient Http, Guid DocId)> FiledPdfAsync(byte[] pdfBytes)
    {
        var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = $"ocr-{Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".pdf" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(pdfBytes))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return (http, docId);
    }

    private async Task<JsonElement> VersionsAsync(HttpClient http, Guid docId) =>
        (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/versions")).GetProperty("versions");

    // The worker polls; OCR takes seconds. Poll the versions list the clients already read, with a bounded
    // patience — the sidecar is real, so a wedge here is a product symptom, not a test one.
    private async Task<JsonElement?> WaitForAsync(HttpClient http, Guid docId, Func<JsonElement, bool> done, int seconds = 120)
    {
        for (var i = 0; i < seconds; i++)
        {
            var versions = await VersionsAsync(http, docId);
            if (done(versions))
            {
                return versions;
            }

            await Task.Delay(1000);
        }

        return null;
    }

    [Fact]
    public async Task An_image_only_pdf_filed_directly_gets_its_searchable_successor()
    {
        var (http, docId) = await FiledPdfAsync(ImagePdf.ImageOnly());

        // The whole automatic path: enqueue at finalize → worker detects → verdict persisted → successor
        // filed. Version 2's arrival is the issue's "expected"; the verdict is what makes a non-arrival
        // explicable from the UI ever after.
        var versions = await WaitForAsync(http, docId, v => v.GetArrayLength() >= 2);
        Assert.True(versions is not null, "No searchable successor appeared within the patience window — the direct-filing OCR path is broken (#999 defect 2).");

        var v1 = versions.Value.EnumerateArray().Single(v => v.GetProperty("versionNumber").GetInt32() == 1);
        Assert.Equal("ConvertibleScan", v1.GetProperty("ocrVerdict").GetString());
        var v2 = versions.Value.EnumerateArray().Single(v => v.GetProperty("versionNumber").GetInt32() == 2);
        Assert.Equal(".pdf", v2.GetProperty("fileExtension").GetString());

        http.Dispose();
    }

    [Fact]
    public async Task A_born_digital_pdf_is_judged_not_a_scan_until_the_user_forces_it()
    {
        var (http, docId) = await FiledPdfAsync(ImagePdf.TextOnly());

        // The automatic path judges and declines — and now SAYS so, on the version, instead of only in a log.
        var judged = await WaitForAsync(http, docId,
            v => v.EnumerateArray().Any(x => x.GetProperty("ocrVerdict").ValueKind == JsonValueKind.String), seconds: 60);
        Assert.True(judged is not null, "The worker never judged the PDF — verdict persistence is broken.");
        var v1 = judged.Value.EnumerateArray().Single();
        Assert.Equal("NotAScan", v1.GetProperty("ocrVerdict").GetString());

        // The user overrules the detector (Make searchable): follow the rel the version advertises. This is
        // the escape hatch for the detector-blind field case — a verdict is advice, not a verdict on appeal.
        var rel = v1.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "make-searchable");
        (await http.PostAsync(rel.GetProperty("href").GetString(), null)).EnsureSuccessStatusCode();

        var forced = await WaitForAsync(http, docId, v => v.GetArrayLength() >= 2);
        Assert.True(forced is not null, "The forced conversion produced no successor — the force path is broken.");

        http.Dispose();
    }
}
