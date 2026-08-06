using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// End-to-end for the SignalR Valkey backplane (ADR "SignalR Valkey backplane"): a notification produced on one API
// instance reaches a client connected to a DIFFERENT instance, proving the push fans out across replicas. Both
// hosts share the same Postgres + Valkey (via the collection fixture's process-global env), so a second in-process
// WebApplicationFactory stands in for a second replica.
[Collection(E2ECollection.Name)]
public class RealtimeBackplaneTests
{
    private readonly E2EApiFactory _factory;

    public RealtimeBackplaneTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_notification_on_one_replica_reaches_a_client_connected_to_another()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"backplane-{Guid.NewGuid():N}@e2e.local";
        const string password = "backplane1234";
        var recipientId = await _factory.SeedUserAsync(tenantId, email, password, "Recipient");
        var recipientToken = await _factory.GetUserTokenAsync(email, password);

        // Connect the recipient's hub to replica A (the shared factory).
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(recipientToken);
            })
            .Build();
        connection.On<PushDto>("notification", n => received.TrySetResult(n.Title));
        await connection.StartAsync();

        // Replica B — a second in-process host sharing the same Postgres + Valkey backplane. Produce a notification
        // there by inserting a row through B's DbContext, whose post-commit push choke point (ADR 0427) fans out
        // over Valkey.
        await using var replicaB = new WebApplicationFactory<Program>();
        using (var scope = replicaB.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RecipientUserId = recipientId,
                Type = NotificationType.ChatMessagePosted,
                Title = "cross-replica",
                Body = "delivered via the Valkey backplane",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // The client on replica A receives the push produced by replica B.
        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(completed == received.Task, "a notification produced on replica B should reach the client connected to replica A");
        Assert.Equal("cross-replica", await received.Task);
    }

    private sealed record PushDto(string Title, string Body);
}
