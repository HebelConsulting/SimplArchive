using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of notification preferences (ADR "Notification preferences"): the account menu opens the
// preferences dialog; toggling a type's switch off and saving persists it. The browser drives the real UI; the
// result is asserted (and the shared demo admin restored) over the API for robustness. The escalation types
// aren't listed — only the mutable ones (7 now, incl. SubscribedActivity).
[Collection(UiCollection.Name)]
public class WebNotificationPreferencesTests
{
    private readonly SelfHostedAppFixture _app;

    public WebNotificationPreferencesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Account_menu_dialog_mutes_a_type_and_it_persists()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Deterministic start: all mutable types emailed.
        await SetAllAsync(http, true);

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Notification preferences").First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.GetByText("A comment is posted on my document")).ToBeVisibleAsync();

        // Toggle the CommentPosted switch off (the 5th, in the fixed policy order) and save.
        await dialog.Locator(".mud-switch").Nth(4).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // Persisted: the CommentPosted (type 4) email channel is now off.
        await Eventually(async () =>
        {
            var prefs = await GetAsync(http);
            Assert.False(prefs.Single(p => p.GetProperty("type").GetInt32() == 4).GetProperty("emailEnabled").GetBoolean());
        });

        // Restore the shared demo admin to all-on.
        await SetAllAsync(http, true);
    }

    private static async Task<JsonElement[]> GetAsync(HttpClient http) =>
        (await http.GetFromJsonAsync<JsonElement>("/api/notifications/preferences")).GetProperty("preferences").EnumerateArray().ToArray();

    private static async Task SetAllAsync(HttpClient http, bool enabled)
    {
        var prefs = await GetAsync(http);
        var body = new { preferences = prefs.Select(p => new { type = p.GetProperty("type").GetInt32(), emailEnabled = enabled }) };
        (await http.PutAsJsonAsync("/api/notifications/preferences", body)).EnsureSuccessStatusCode();
    }

    // A tiny retry so the assertion tolerates the save round-trip.
    private static async Task Eventually(Func<Task> assertion)
    {
        for (var i = 0; i < 20; i++)
        {
            try { await assertion(); return; }
            catch (Xunit.Sdk.XunitException) { await Task.Delay(250); }
        }

        await assertion();
    }
}
