using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of document tags (ADR "Document tags"): the demo admin adds a free-form tag on the detail-pane
// Edit → Save, and the read-only tag chip appears.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebDocumentTagsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDocumentTagsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Add_a_tag_from_the_detail_pane_and_see_the_chip()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"tags-{suffix}";
        var tag = $"dt{suffix}"; // a fresh, lowercase, valid tag

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("tagged content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        var detail = page.Locator("[data-pane='index']");
        await detail.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();

        // Type the tag into the Tags add box, then click the explicit Add button — a deterministic commit path
        // (MudAutocomplete's Enter binding can lag under CI's headless Chrome). Confirm the value bound first.
        var tagsRow = detail.Locator(".wb-mask-row").Filter(new() { HasText = "Tags" });
        var addBox = tagsRow.Locator("input").First;
        await addBox.ClickAsync();
        await addBox.PressSequentiallyAsync(tag);
        await Expect(addBox).ToHaveValueAsync(tag);
        await tagsRow.GetByRole(AriaRole.Button, new() { Name = "Add tag" }).ClickAsync();

        // The editable chip shows in edit mode once the tag is added — assert it before saving so a lost add
        // fails here (clearly) rather than after a no-op Save.
        await Expect(tagsRow.GetByText(tag)).ToBeVisibleAsync();

        await detail.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // The read-only chip shows after save.
        await Expect(detail.GetByText(tag)).ToBeVisibleAsync();
    }
}
