using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of search facets (ADR "Search facets"). Setup is over the API (three documents sharing a unique
// content word, two of one mask + one of another); indexing is awaited over the API, then the browser drives
// the Search tab's facet panel: a document-type facet appears and clicking it drills the results down.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSearchFacetsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSearchFacetsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Facet_panel_appears_and_document_type_drills_down()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var word = $"webfacet{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var masks = (await http.GetFromJsonAsync<JsonElement>("/api/masks")).GetProperty("masks").EnumerateArray().ToArray();
        // Two masks that are genuinely assignable to a plain document, picked BY NAME because an
        // index-based pick is not stable against the well-known set growing. The pick has to clear two
        // separate refusals: a TYPED mask (Note/Contact/Calendar) is admitted only inside its own folder
        // (containment, #564/ADR 0619), and a mask with REQUIRED fields — eMail wants From/To/Subject —
        // is refused on assignment until they are filled (ADR 0176). Basic Entry and Folder clear both.
        var maskA = masks.First(m => m.GetProperty("name").GetString() == "Basic Entry");
        var maskB = masks.First(m => m.GetProperty("name").GetString() == "Folder");
        var (maskAId, maskAName) = (maskA.GetProperty("id").GetGuid(), maskA.GetProperty("name").GetString()!);

        var repoId = await PostIdAsync(http, "/api/repositories", new { name = $"webfacets-{suffix}" });
        await UploadClassifiedAsync(http, repoId, $"a1-{suffix}", word, maskAId);
        await UploadClassifiedAsync(http, repoId, $"a2-{suffix}", word, maskAId);
        await UploadClassifiedAsync(http, repoId, $"b1-{suffix}", word, maskB.GetProperty("id").GetGuid());

        // Wait (over the API) until all three are indexed, so the browser search sees results + facets.
        await Eventually(async () =>
        {
            var ids = (await http.GetFromJsonAsync<JsonElement>($"/api/search?q={word}")).GetProperty("results").EnumerateArray().Count();
            Assert.Equal(3, ids);
        });

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();
        await page.FillAsync(".wb-search-query input", word); // the query row moved below the ribbon (post-#530 review round)
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();

        // The three results and the facet panel appear.
        await Expect(page.Locator(".wb-search-results").GetByText($"b1-{suffix}").First).ToBeVisibleAsync();
        var typeChip = page.Locator(".wb-search-facets").GetByText(maskAName, new() { Exact = false }).First;
        await Expect(typeChip).ToBeVisibleAsync();

        // A File type facet dimension appears (all three are .txt).
        await Expect(page.Locator(".wb-search-facets").GetByText("File type")).ToBeVisibleAsync();

        // Drill down by the maskA document type → the maskB document drops out, a maskA one stays.
        await typeChip.ClickAsync();
        await Expect(page.Locator(".wb-search-results").GetByText($"b1-{suffix}").First).ToBeHiddenAsync();
        await Expect(page.Locator(".wb-search-results").GetByText($"a1-{suffix}").First).ToBeVisibleAsync();

        // Post-filter faceting: the Type dimension stays open — the maskB type chip is still shown after drilling
        // (so the user can broaden the selection), rather than the dimension collapsing to the chosen value.
        var maskBName = maskB.GetProperty("name").GetString()!;
        await Expect(page.Locator(".wb-search-facets").GetByText(maskBName, new() { Exact = false }).First).ToBeVisibleAsync();
    }

    private static async Task UploadClassifiedAsync(HttpClient http, Guid repoId, string name, string content, Guid maskId)
    {
        var docId = await PostIdAsync(http, $"/api/documents/{repoId}/children", new { name });
        var created = await PostAsync(http, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> PostIdAsync(HttpClient http, string url, object body) =>
        (await PostAsync(http, url, body)).GetProperty("id").GetGuid();

    private static async Task Eventually(Func<Task> assertion)
    {
        for (var i = 0; i < 90; i++)
        {
            try { await assertion(); return; }
            catch (Xunit.Sdk.XunitException) { await Task.Delay(1000); }
        }

        await assertion();
    }
}
