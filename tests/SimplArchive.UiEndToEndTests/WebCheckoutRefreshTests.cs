using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Entering the Check-out tab shows what is checked out NOW, not what was checked out when the page loaded
// (reported from use, #762 follow-up).
//
// The check-out is made OUT OF BAND — through the API, with the page already open — because a check-out made
// through the UI refreshes the shell's list as a side effect of the action, and a test that did that would pass
// against the bug. The interesting case is the one the page cannot know about, and it is the ordinary one:
// saving over WebDAV is an implicit check-out (ADR 0562), so the tab that exists to show those was precisely
// the tab that could not see them.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebCheckoutRefreshTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCheckoutRefreshTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Entering_the_checkout_tab_shows_a_check_out_made_elsewhere()
    {
        var page = await Ui.LoginAsync(_app);
        var name = $"refresh-{Guid.NewGuid():N}"[..14];

        // Visit the tab FIRST, so the shell's list is loaded and would happily stay that way.
        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        await Expect(page.Locator(".wb-checkout")).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-checkout .wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();

        // …then check a document out from outside the page entirely.
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var created = await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>();
        var documentId = created.GetProperty("id").GetGuid();

        // It needs CONTENT before it can be checked out — a document with no confirmed version is refused with
        // NothingToCheckOutException, which is right: there is nothing to take a working copy of.
        var version = await (await http.PostAsJsonAsync($"/api/documents/{documentId}/versions", new { fileExtension = ".txt" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString(),
                new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("checked out elsewhere")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{documentId}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        (await http.PutAsync($"/api/documents/{documentId}/checkout", null)).EnsureSuccessStatusCode();

        // Leave and come back. Entering the tab must re-ask; the component renders the shell's list and has no
        // way to fetch one of its own, which is why only this tab went stale — its sibling IntrayTab loads
        // itself and is recreated on every switch.
        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await Expect(page.Locator("[data-pane='list']")).ToBeVisibleAsync();
        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();

        await Expect(page.Locator(".wb-checkout .wb-list-row").Filter(new() { HasText = name })).ToBeVisibleAsync();
    }
}
