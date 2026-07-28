using System.Net.Http.Headers;
using System.Net.Http.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of real-time notifications (ADR "Real-time notifications (SignalR)"): the RealtimeNotificationClient
// connects the hub over a real WebSocket (?access_token=), and a notification triggered for that user is pushed
// to it live. Exercises the WS handshake + query-string-token path that the container E2E's long-polling doesn't.
[Collection(UiCollection.Name)]
public class DesktopRealtimeNotificationsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopRealtimeNotificationsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Realtime_client_receives_a_pushed_notification()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var adminToken = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var admin = new SimplArchiveApiClient(adminToken);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A throwaway recipient user.
        var email = $"rt-{suffix}@example.test";
        var userId = await admin.CreateUserAsync(email, "RT User " + suffix);
        var password = await admin.ResetUserPasswordAsync(userId);
        var userToken = await Ui.GetUserTokenAsync(_app.BaseUrl, email, password);

        // Connect the recipient's realtime client (real WebSocket + ?access_token=).
        var received = new TaskCompletionSource<RealtimeNotificationClient.RealtimeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var realtime = new RealtimeNotificationClient(_app.BaseUrl, userToken);
        realtime.NotificationReceived += n => received.TrySetResult(n);
        await realtime.StartAsync();

        // The admin creates a repo and grants the recipient access → an AccessGranted notification, pushed live.
        await admin.CreateRepositoryAsync($"rt-{suffix}");
        var repo = (await admin.GetRepositoriesAsync()).First(r => r.Name == $"rt-{suffix}");
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        (await http.PutAsJsonAsync($"api/documents/{repo.Id}/acl-entries/users/{userId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(completed == received.Task, "the desktop realtime client should receive a pushed notification after the grant");
        Assert.False(string.IsNullOrWhiteSpace((await received.Task).Title));
    }
}
