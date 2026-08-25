using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web watermark (ADR "Document watermarking"): previewing a Confidential document overlays a sensitivity
// watermark; a Public/unclassified document does not.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebWatermarkTests
{
    private readonly SelfHostedAppFixture _app;

    public WebWatermarkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Confidential_preview_shows_a_watermark_public_does_not()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var labels = (await http.GetFromJsonAsync<JsonElement>("/api/sensitivity-labels")).GetProperty("labels");
        Guid LabelId(string n) => labels.EnumerateArray().Single(l => l.GetProperty("name").GetString() == n).GetProperty("id").GetGuid();

        var confidential = await SeedTextDocAsync(http, repoId, $"wm-conf-{tag}", LabelId("Confidential")); // watermarked
        var publicName = await SeedTextDocAsync(http, repoId, $"wm-pub-{tag}", LabelId("Public"));           // not watermarked

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var watermark = page.Locator("[data-pane='preview'] .wb-watermark");

        // The Confidential document's preview shows the watermark.
        await list.Locator(".wb-list-row").Filter(new() { HasText = confidential }).ClickAsync();
        await Expect(watermark).ToBeVisibleAsync();

        // The Public document's preview does not.
        await list.Locator(".wb-list-row").Filter(new() { HasText = publicName }).ClickAsync();
        await Expect(watermark).Not.ToBeVisibleAsync();
    }

    private static async Task<string> SeedTextDocAsync(HttpClient http, Guid repoId, string name, Guid labelId)
    {
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"content {name}")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/api/documents/{docId}/sensitivity", new { labelId })).EnsureSuccessStatusCode();
        return name;
    }
}
