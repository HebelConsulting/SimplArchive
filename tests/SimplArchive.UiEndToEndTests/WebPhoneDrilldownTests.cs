using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Phone single-pane drill-down (ADR "Responsive phone drill-down"): at ≤767px the tree is a drawer, the folder
// contents list is the default view, and tapping a document opens a full-screen detail overlay with
// Preview/Details/Comments sub-tabs. Verifies the drawer → drill-in → detail overlay → sub-tab → back flow.
[Collection(UiCollection.Name)]
public class WebPhoneDrilldownTests
{
    private readonly SelfHostedAppFixture _app;

    public WebPhoneDrilldownTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Drill_down_navigation_on_a_phone_viewport()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"phone-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("phone content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.SetViewportSizeAsync(390, 844); // iPhone-ish portrait
        await page.WaitForTimeoutAsync(400); // resize debounce → _isPhone reported to Blazor

        // The phone top bar is shown; open the tree drawer via the hamburger, then drill into the repository.
        await Expect(page.Locator(".wb-phone-topbar")).ToBeVisibleAsync();
        await page.Locator(".wb-phone-topbar [aria-label='Folders']").ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync(); // drills in + closes the drawer

        // A single tap on the document row opens the full-screen detail overlay (no side-by-side on a phone).
        var row = page.Locator("[data-pane='list']").Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();
        await Expect(page.Locator(".wb-detail.wb-phone-detail-open")).ToBeVisibleAsync();

        // The Details sub-tab switches the overlay to the mask/index pane.
        await page.Locator(".wb-phone-detbar").GetByText("Details").ClickAsync();
        await Expect(page.Locator(".wb-detail [data-pane='index']")).ToBeVisibleAsync();

        // Back returns to the folder list (the overlay closes).
        await page.Locator(".wb-phone-detbar [aria-label='Back']").ClickAsync();
        await Expect(page.Locator(".wb-detail.wb-phone-detail-open")).ToBeHiddenAsync();
        await Expect(row).ToBeVisibleAsync();
    }
}
