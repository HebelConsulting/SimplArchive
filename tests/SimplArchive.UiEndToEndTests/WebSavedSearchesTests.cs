using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of saved searches (ADR "Saved searches"): running a search, clicking Save (naming it via the
// browser prompt), the saved chip appearing, and clicking it to restore + re-run. Cleans up the shared demo
// admin's saved search over the API afterwards.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebSavedSearchesTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSavedSearchesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Save_a_search_and_restore_it_from_the_chip()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var term = $"savedsearch{suffix}";
        var name = $"Saved {suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        try
        {
            var page = await Ui.LoginAsync(_app);
            await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

            // Run a search so "Save" is enabled.
            await page.FillAsync("input[placeholder*='Search by name']", term);
            await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeEnabledAsync();

            // Save it — the name comes from the browser prompt.
            page.Dialog += async (_, dialog) => await dialog.AcceptAsync(name);
            await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

            // The saved-search chip appears.
            var chip = page.Locator(".wb-saved-searches").GetByText(name);
            await Expect(chip).ToBeVisibleAsync();

            // Clear the box, then click the chip → it restores the query and re-runs.
            await page.FillAsync("input[placeholder*='Search by name']", "");
            await chip.ClickAsync();
            await Expect(page.Locator("input[placeholder*='Search by name']")).ToHaveValueAsync(term);

            // Share it (ADR "Scoped saved-search sharing") — the share button opens the scope dialog; pick
            // "Everyone" → Save → the API reflects shareScope 1.
            await page.Locator(".wb-saved-searches").GetByLabel("Share saved search").First.ClickAsync();
            var dialog = page.Locator(".mud-dialog");
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByText("Everyone in the tenant").ClickAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

            var shared = false;
            for (var i = 0; i < 20 && !shared; i++)
            {
                var s = (await http.GetFromJsonAsync<JsonElement>("/api/saved-searches")).GetProperty("savedSearches").EnumerateArray()
                    .FirstOrDefault(x => x.GetProperty("name").GetString() == name);
                shared = s.ValueKind == JsonValueKind.Object && s.GetProperty("shareScope").GetInt32() == 1;
                if (!shared) await Task.Delay(250);
            }
            Assert.True(shared, "the saved search should be shared with everyone after the share dialog");
        }
        finally
        {
            // Remove the demo admin's saved search so the shared tenant is left clean.
            var saved = (await http.GetFromJsonAsync<JsonElement>("/api/saved-searches")).GetProperty("savedSearches").EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("name").GetString() == name);
            if (saved.ValueKind == JsonValueKind.Object)
            {
                await http.DeleteAsync($"/api/saved-searches/{saved.GetProperty("id").GetGuid()}");
            }
        }
    }
}
