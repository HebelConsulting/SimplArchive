using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the "My work" dashboard (ADR "My work dashboard"): the tab shows the caller's due-soon
// reminders + followed documents, and an item navigates to its document.
[Collection(UiCollection.Name)]
public class WebMyWorkTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMyWorkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Dashboard_shows_reminders_and_following_and_navigates()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var name = $"mw-{tag}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // A confirmed version so it's a real document (selecting it populates the detail pane on navigation).
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        // A due-soon reminder + follow the document.
        (await http.PostAsJsonAsync($"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), note = "Check", recurrence = 0 })).EnsureSuccessStatusCode();
        (await http.PutAsync($"/api/documents/{docId}/subscription", null)).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"My work\"]").First.ClickAsync();

        var dash = page.Locator(".wb-mywork");
        await Expect(dash).ToBeVisibleAsync();
        // The document appears under both Reminders and Following.
        await Expect(dash.GetByText(name).First).ToBeVisibleAsync();

        // Clicking a dashboard item navigates to the document (switches to Repositories + selects it).
        await dash.GetByText(name).First.ClickAsync();
        await Expect(page.Locator("[data-pane='index']").GetByText(name).First).ToBeVisibleAsync();
    }
}
