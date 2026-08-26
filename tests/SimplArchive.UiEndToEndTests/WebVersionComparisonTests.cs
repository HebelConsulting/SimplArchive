using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of version comparison (ADR "Document version comparison"): a document with two text versions is
// seeded via the API, then the UI opens "Compare versions" from the detail pane and shows the inline diff.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebVersionComparisonTests
{
    private readonly SelfHostedAppFixture _app;

    public WebVersionComparisonTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Compare_versions_shows_an_inline_diff()
    {
        var name = "cmp-" + Guid.NewGuid().ToString("N")[..8];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Seed a document with two text versions in the demo repository.
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await AddVersionAsync(http, docId, "apple\nbanana\ncherry\n");
        await AddVersionAsync(http, docId, "apple\nBANANA split\ncherry\ndate\n");

        // Drive the UI: open the doc, click Compare versions, Compare, see the added line.
        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Compare versions" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Nothing runs on open (ADR "Explicit compare", issue #371) — the result area shows the hint.
        var hint = dialog.Locator("[data-testid='compare-hint']");
        await Expect(hint).ToBeVisibleAsync();

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Compare", Exact = true }).ClickAsync();

        // The diff shows the added line (with its "+" marker) and the removed line. The result panel is also
        // asserted BY ITS TEST ID, because the manual's capture waits on that id to know the comparison has
        // finished — a hook nothing asserts is one that gets renamed, and the figure would go back to being a
        // picture of the spinner.
        await Expect(dialog.Locator("[data-testid='compare-diff']")).ToBeVisibleAsync();
        await Expect(dialog.GetByText("+ BANANA split")).ToBeVisibleAsync();
        await Expect(dialog.GetByText("- banana")).ToBeVisibleAsync();
        await Expect(hint).ToBeHiddenAsync();

        // Changing a picker discards the diff and returns to the hint, so a stale diff is never attributed to the
        // new selection. Picking the SAME version on both sides also disables Compare.
        await dialog.Locator(".mud-input-control").First.ClickAsync(); // the "From" MudSelect opens via its input-control
        await page.Locator(".mud-list-item").Filter(new() { HasText = "v2" }).First.ClickAsync();

        await Expect(hint).ToBeVisibleAsync();
        await Expect(dialog.GetByText("+ BANANA split")).ToBeHiddenAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Compare", Exact = true })).ToBeDisabledAsync();
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
