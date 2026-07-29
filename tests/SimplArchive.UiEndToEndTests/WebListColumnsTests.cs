using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of list-row columns (ADR "List-row columns and sorting"): the contents list shows a sortable
// column header, and clicking the Name header toggles the row order.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebListColumnsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebListColumnsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Column_header_shows_and_sorts_by_name()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var folderName = $"cols-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        // An isolated subfolder with two children whose names sort a-before-b.
        var folderId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = folderName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await http.PostAsJsonAsync($"/api/documents/{folderId}/children", new { name = $"bbb-{suffix}" });
        await http.PostAsJsonAsync($"/api/documents/{folderId}/children", new { name = $"aaa-{suffix}" });

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        // Widen the list pane so all columns show — the pane-adaptive columns (ADR "Pane-adaptive contents
        // columns") collapse to Name + ⋮ at the default ~300px width, so column-header sorting (the ephemeral
        // secondary sort) requires a pane wide enough to reveal the Type/Size/Tags headers.
        await list.EvaluateAsync("el => el.style.flex = '0 0 680px'");
        await list.Locator(".wb-list-row").Filter(new() { HasText = folderName }).DblClickAsync(); // drill into the subfolder

        // The sortable header is visible.
        var header = list.Locator(".wb-chead");
        await Expect(header).ToBeVisibleAsync();
        await Expect(header).ToContainTextAsync("Type");
        await Expect(header).ToContainTextAsync("Tags");

        // Switch the active column first (to Size), so the next Name click deterministically sets Name ascending.
        await header.GetByText("Size").ClickAsync();

        // Sort by Name ascending → aaa before bbb.
        await header.GetByText("Name").ClickAsync();
        await Expect(list.Locator(".wb-list-row").First).ToContainTextAsync($"aaa-{suffix}");

        // Click again → descending → bbb before aaa.
        await header.GetByText("Name").ClickAsync();
        await Expect(list.Locator(".wb-list-row").First).ToContainTextAsync($"bbb-{suffix}");
    }
}
