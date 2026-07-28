using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of document reminders (ADR "Document reminders"): the detail-pane Remind button opens the
// dialog; setting a reminder (the date defaults to tomorrow) persists it, cross-checked against the backend.
[Collection(UiCollection.Name)]
public class WebReminderTests
{
    private readonly SelfHostedAppFixture _app;

    public WebReminderTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Set_a_reminder_from_the_detail_pane()
    {
        var name = $"rem-{Guid.NewGuid().ToString("N")[..8]}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        async Task<int> ReminderCountAsync() =>
            (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/reminders")).GetProperty("reminders").GetArrayLength();

        Assert.Equal(0, await ReminderCountAsync());

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).ClickAsync();

        // Open the Remind dialog (the date defaults to tomorrow) and set the reminder.
        await page.Locator("[data-pane='index']").GetByRole(AriaRole.Button, new() { Name = "Remind" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Set reminder" }).ClickAsync();
        await Expect(page.GetByText("Reminder set.")).ToBeVisibleAsync();

        // The backend now has the reminder.
        Assert.Equal(1, await ReminderCountAsync());
    }
}
