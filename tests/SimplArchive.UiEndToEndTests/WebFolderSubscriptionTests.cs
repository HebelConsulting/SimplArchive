using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of folder / subtree subscriptions (ADR "Folder / subtree subscriptions"): selecting a folder
// reveals a Follow bell in the comment-pane header that subscribes to the folder (and its whole subtree),
// cross-checked against the backend.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebFolderSubscriptionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebFolderSubscriptionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Folder_follow_bell_subscribes_and_unsubscribes()
    {
        var name = $"watch-{Guid.NewGuid().ToString("N")[..8]}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        // A folder = a child document with no versions.
        var folderId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        async Task<bool> SubscribedAsync() =>
            (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{folderId}/subscription")).GetProperty("subscribed").GetBoolean();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).ClickAsync();

        var follow = page.Locator("[data-pane='chat']").GetByRole(AriaRole.Button, new() { Name = "Follow folder" });
        await Expect(follow).ToBeVisibleAsync();

        await follow.ClickAsync();
        await Expect(page.GetByText("Following this folder and everything in it.")).ToBeVisibleAsync();
        Assert.True(await SubscribedAsync());

        await follow.ClickAsync();
        await Expect(page.GetByText("Unfollowed folder.")).ToBeVisibleAsync();
        Assert.False(await SubscribedAsync());
    }
}
