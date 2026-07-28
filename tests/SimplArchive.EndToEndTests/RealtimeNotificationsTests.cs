using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace SimplArchive.EndToEndTests;

// End-to-end for real-time notifications (ADR "Real-time notifications (SignalR)") over the real API + Postgres:
// a User connects the /hubs/notifications SignalR hub (authenticated), then another principal triggers a
// notification for them (an ACL grant → AccessGranted), and the push reaches the live connection. Proves the hub
// auth (token from the connection), the DbContext push choke point, and per-user targeting end-to-end.
[Collection(E2ECollection.Name)]
public class RealtimeNotificationsTests
{
    private readonly E2EApiFactory _factory;

    public RealtimeNotificationsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_grant_pushes_a_live_notification_to_the_recipients_hub_connection()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"realtime-{Guid.NewGuid():N}@e2e.local";
        const string password = "realtime1234";
        var recipientId = await _factory.SeedUserAsync(tenantId, email, password, "Recipient");
        var recipientToken = await _factory.GetUserTokenAsync(email, password);

        // Connect the recipient's hub (over the in-memory TestServer handler; SignalR uses long-polling there).
        var received = new TaskCompletionSource<(string Title, string Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(recipientToken);
            })
            .Build();
        connection.On<PushDto>("notification", n => received.TrySetResult((n.Title, n.Body)));
        await connection.StartAsync();

        // Trigger: the owner grants the recipient access to a doc → an AccessGranted notification, pushed live.
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"RT {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{recipientId}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        // The live connection receives the push within a few seconds.
        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(completed == received.Task, "the hub connection should receive a pushed notification after the grant");
        var (title, _) = await received.Task;
        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    private sealed record PushDto(string Title, string Body);
}
