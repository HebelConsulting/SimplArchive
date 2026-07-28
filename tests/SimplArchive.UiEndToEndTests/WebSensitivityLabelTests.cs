using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of sensitivity labels (ADR "Data classification / sensitivity labels"): the demo admin sets a
// document's sensitivity via the detail-pane Edit → Save, and the badge appears.
[Collection(UiCollection.Name)]
public class WebSensitivityLabelTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSensitivityLabelTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Set_the_label_from_the_detail_pane_and_see_the_badge()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var name = $"sens-{tag}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        // A real document (with a confirmed version) so the detail pane shows the Edit toggle.
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("classified content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        // Edit → set Sensitivity = Confidential → Save.
        var detail = page.Locator("[data-pane='index']");
        await detail.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        var sensRow = detail.Locator(".wb-mask-row").Filter(new() { HasText = "Sensitivity" });
        await sensRow.Locator(".mud-input-control").First.ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Confidential" }).ClickAsync();
        await detail.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // The read-only badge now shows Confidential.
        await Expect(detail.GetByText("Confidential")).ToBeVisibleAsync();

        // The list row shows a sensitivity badge too (ADR "Sensitivity-label list badge + search facet") —
        // reload the folder to pick up the saved label.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var badgedRow = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(badgedRow).ToContainTextAsync("Confidential");
    }
}
