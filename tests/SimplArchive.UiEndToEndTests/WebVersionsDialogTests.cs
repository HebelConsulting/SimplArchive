using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web Versions dialog (ADR "Versions dialog"): a document with two versions is seeded via the API, then the
// UI opens the ribbon "Versions" dialog, sees both versions with the latest marked Current, makes the older one
// current (a new version), and confirms the Compare launcher opens.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebVersionsDialogTests
{
    private readonly SelfHostedAppFixture _app;

    public WebVersionsDialogTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Lists_versions_makes_one_current_and_launches_compare()
    {
        var name = "ver-" + Guid.NewGuid().ToString("N")[..8];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await AddVersionAsync(http, docId, "version one content\n");
        await AddVersionAsync(http, docId, "version two content\n");

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        // Open the Versions dialog from the ribbon.
        await page.GetByRole(AriaRole.Button, new() { Name = "Versions", Exact = true }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        // Two version rows, and the latest is labelled Current (exact, so it doesn't also match "Make current").
        await Expect(dialog.Locator(".wb-version-row")).ToHaveCountAsync(2);
        await Expect(dialog.GetByText("Current", new() { Exact = true })).ToBeVisibleAsync();

        // Make-current is single-select + confirm: select the older version (last row), click the single "Make
        // current" action, then confirm the message box. The pointer approach (issue #265) pins the existing
        // version — no new version — so the list stays at two, with the older one now labelled Current.
        await dialog.Locator(".wb-version-row").Last.ClickAsync();
        await dialog.Locator(".wb-version-makecurrent").ClickAsync();
        await page.RunAndWaitForResponseAsync(async () =>
        {
            // The confirmation is a message box on top; its own "Make current" button confirms.
            await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Make current" }).ClickAsync();
        }, r => r.Request.Method == "POST" && r.Url.Contains("/restore") && r.Status is >= 200 and < 300);
        await Expect(dialog.Locator(".wb-version-row")).ToHaveCountAsync(2);

        // The Compare launcher opens the compare dialog.
        await dialog.Locator(".wb-version-compare").ClickAsync();
        await Expect(page.GetByText("Compare versions —")).ToBeVisibleAsync();
    }

    private static async Task AddVersionAsync(HttpClient http, Guid docId, string content)
    {
        var created = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { })).EnsureSuccessStatusCode();
    }
}
