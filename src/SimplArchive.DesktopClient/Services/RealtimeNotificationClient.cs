using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace SimplArchive.DesktopClient.Services;

// The desktop SignalR client for real-time notifications (ADR "Real-time notifications (SignalR)"). Connects to
// the Api's /hubs/notifications with the caller's access token (passed as ?access_token=, since the token is a
// bearer credential) and raises NotificationReceived when the server pushes one, so the bell updates live.
// Best-effort: a failed connect never breaks the app (the bell still loads on login).
public sealed class RealtimeNotificationClient : IAsyncDisposable
{
    private readonly string _baseUrl;
    private readonly string _accessToken;
    private HubConnection? _hub;

    public RealtimeNotificationClient(string baseUrl, string accessToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _accessToken = accessToken;
    }

    // Raised (off the connection thread) with the pushed notification's title + body.
    public event Action<RealtimeNotification>? NotificationReceived;

    public async Task StartAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{_baseUrl}/hubs/notifications", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_accessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<RealtimeNotification>("notification", n => NotificationReceived?.Invoke(n));
        await _hub.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    public sealed record RealtimeNotification(string Title, string Body);
}
