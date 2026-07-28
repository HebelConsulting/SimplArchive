using System.Net.Http;
using System.Net.Sockets;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The session-reconnect logic (ADR "Desktop session reconnect"): connectivity failures are classified apart
// from genuine crashes (only the former drive the reconnect modal), and the background heartbeat raises the
// connection-lost callback when a probe finds the server unreachable.
public class DesktopSessionReconnectTests
{
    [Fact]
    public void Connectivity_errors_are_classified_apart_from_genuine_crashes()
    {
        // Connectivity failures — the server is unreachable — drive the reconnect modal.
        Assert.True(AppExceptions.IsConnectivityError(new HttpRequestException("refused")));
        Assert.True(AppExceptions.IsConnectivityError(new SocketException()));
        Assert.True(AppExceptions.IsConnectivityError(new TaskCanceledException("timeout")));
        Assert.True(AppExceptions.IsConnectivityError(new TimeoutException()));
        // Nested inside another exception still counts.
        Assert.True(AppExceptions.IsConnectivityError(new InvalidOperationException("wrap", new HttpRequestException())));

        // Genuine bugs / business errors do NOT — they keep the one-shot crash dialog.
        Assert.False(AppExceptions.IsConnectivityError(new NullReferenceException()));
        Assert.False(AppExceptions.IsConnectivityError(new ApiActionException("business rule")));
        Assert.False(AppExceptions.IsConnectivityError(new InvalidOperationException("plain")));
    }

    [Fact]
    public async Task Heartbeat_raises_connection_lost_only_when_unreachable()
    {
        var lost = 0;

        var unreachable = new SessionHeartbeat(() => lost++, _ => Task.FromResult(false));
        await unreachable.TickAsync();
        Assert.Equal(1, lost);

        var reachable = new SessionHeartbeat(() => lost++, _ => Task.FromResult(true));
        await reachable.TickAsync();
        Assert.Equal(1, lost); // unchanged — a reachable probe raises nothing
    }
}
