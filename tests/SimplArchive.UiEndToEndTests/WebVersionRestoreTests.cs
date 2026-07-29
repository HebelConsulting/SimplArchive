using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of version restore (ADR "Version restore"): the Compare-versions dialog's Restore button rolls
// back to the selected version, cross-checked against the backend (a new current version with the old content).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebVersionRestoreTests
{
    private readonly SelfHostedAppFixture _app;

    public WebVersionRestoreTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Restore_from_the_compare_dialog_makes_the_old_content_current()
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

        // Open Compare versions → Restore the "From" version (defaults to v1).
        await page.Locator("[data-pane='index']").GetByRole(AriaRole.Button, new() { Name = "Compare versions" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Restore v1" }).ClickAsync();
        await Expect(page.GetByText("Restored v1 as the current version.")).ToBeVisibleAsync();

        // A third (restored) version now exists whose content is version 1's (content A).
        Assert.Equal(3, await VersionCountAsync());
        var versions = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray().ToList();
        var newest = versions.OrderByDescending(v => v.GetProperty("versionNumber").GetInt32()).First();
        var download = newest.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString();
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
