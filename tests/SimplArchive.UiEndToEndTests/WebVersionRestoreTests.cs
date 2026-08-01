using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of version restore (ADR "Version-restore via a current-version pointer", issue #265): the Versions
// dialog's "Make current" pins an existing version via the pointer — cross-checked against the backend that NO new
// version is created and the older version's content is served as current.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebVersionRestoreTests
{
    private readonly SelfHostedAppFixture _app;

    public WebVersionRestoreTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Make_current_from_the_versions_dialog_serves_the_old_content_without_a_new_version()
    {
        var name = $"restore-{Guid.NewGuid().ToString("N")[..8]}";
        var contentA = $"A-{Guid.NewGuid():N}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await AddVersionAsync(http, docId, contentA);
        await AddVersionAsync(http, docId, $"B-{Guid.NewGuid():N}");

        async Task<int> VersionCountAsync() =>
            (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/versions")).GetProperty("versions").GetArrayLength();
        Assert.Equal(2, await VersionCountAsync());

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).ClickAsync();

        // Open the Versions dialog → select the older version (last row) → Make current + confirm.
        await page.GetByRole(AriaRole.Button, new() { Name = "Versions", Exact = true }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator(".wb-version-row").Last.ClickAsync();
        await dialog.Locator(".wb-version-makecurrent").ClickAsync();
        await page.RunAndWaitForResponseAsync(async () =>
        {
            await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Make current" }).ClickAsync();
        }, r => r.Request.Method == "POST" && r.Url.Contains("/restore") && r.Status is >= 200 and < 300);

        // No new version — still two — and the current version is v1 serving content A.
        Assert.Equal(2, await VersionCountAsync());
        var list = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/versions");
        var currentId = list.GetProperty("currentVersionId").GetGuid();
        var current = list.GetProperty("versions").EnumerateArray().First(v => v.GetProperty("id").GetGuid() == currentId);
        Assert.Equal(1, current.GetProperty("versionNumber").GetInt32());
        var download = current.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString();
        using var storage = new HttpClient();
        Assert.Equal(contentA, await (await storage.GetAsync(download)).Content.ReadAsStringAsync());
    }

    private static async Task AddVersionAsync(HttpClient http, Guid docId, string content)
    {
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
    }
}
