using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of bulk actions (ADR "Bulk actions on selected documents"): Ctrl-clicking rows builds a
// multi-selection (no checkboxes) that reveals a bulk-action bar; Delete moves them all to the recycle bin.
[Collection(UiCollection.Name)]
public class WebBulkActionsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebBulkActionsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Multi_select_and_bulk_delete()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var names = new[] { $"bulk-{suffix}-a", $"bulk-{suffix}-b" };
        foreach (var name in names)
        {
            (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).EnsureSuccessStatusCode();
        }

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var rowA = list.Locator(".wb-list-row").Filter(new() { HasText = names[0] });
        var rowB = list.Locator(".wb-list-row").Filter(new() { HasText = names[1] });
        await Expect(rowA).ToBeVisibleAsync();

        // Ctrl-click both rows (a synthetic click carrying ctrlKey — reliably read by Blazor's handler) to
        // build the multi-selection with no checkboxes → the bulk-action bar appears.
        var ctrl = new Dictionary<string, object> { ["ctrlKey"] = true, ["bubbles"] = true };
        await rowA.DispatchEventAsync("click", ctrl);
        await rowB.DispatchEventAsync("click", ctrl);
        await Expect(list.Locator(".wb-bulk-bar")).ToContainTextAsync("2 selected");

        // Delete → confirm (the dialog's Delete button) → both rows are gone from the listing.
        await list.Locator(".wb-bulk-bar").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

        await Expect(rowA).ToHaveCountAsync(0);
        await Expect(rowB).ToHaveCountAsync(0);
    }
}
