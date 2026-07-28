using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of real-time notifications (ADR "Real-time notifications (SignalR)"): the demo admin's browser
// sits idle at zero unread, a second user comments on a document the admin owns, and the notification bell badge
// appears LIVE — no reload, no poll (the client only loads on login/open, so a live badge change can only be the
// SignalR push).
[Collection(UiCollection.Name)]
public class WebRealtimeNotificationsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebRealtimeNotificationsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_new_notification_updates_the_bell_badge_live()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var admin = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Zero the admin's unread so the badge is hidden at login (the demo seed leaves one review notification).
        (await admin.PostAsync("api/notifications/read-all", null)).EnsureSuccessStatusCode();

        // A second user, granted access, will act on an admin-owned document.
        var email = $"rt-{suffix}@example.test";
        var userId = (await (await admin.PostAsJsonAsync("api/users", new { email, displayName = "RT " + suffix })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var password = (await (await admin.PostAsync($"api/users/{userId}/reset-password", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("password").GetString()!;
        using var user = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl, email, password));

        // The admin creates a repo + doc (admin is the doc creator, so a comment notifies the admin), and grants
        // the second user access to the repo (inherited to the doc).
        var repoId = (await (await admin.PostAsJsonAsync("api/repositories", new { name = $"rt-{suffix}" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var docId = (await (await admin.PostAsJsonAsync($"api/documents/{repoId}/children", new { name = $"rtdoc-{suffix}" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"api/documents/{repoId}/acl-entries/users/{userId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        // The admin logs in (browser); the bell badge is hidden (0 unread). Give the hub a moment to connect.
        var page = await Ui.LoginAsync(_app);
        var badge = page.Locator(".wb-appbar .mud-badge");
        await Expect(badge).ToBeHiddenAsync();
        await page.WaitForTimeoutAsync(2500);

        // The second user comments on the admin's doc → the admin gets a CommentPosted notification, pushed live.
        (await user.PostAsJsonAsync($"api/documents/{docId}/comments", new { body = "live ping" })).EnsureSuccessStatusCode();

        // The badge appears with "1" live — no page reload, proving the SignalR push.
        await Expect(badge).ToHaveTextAsync("1", new() { Timeout = 15000 });
    }
}
