using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

// A background heartbeat that probes the server while the user is logged in (ADR "Desktop session reconnect").
// On an unreachable probe it raises the connection-lost callback — so an idle disconnect surfaces the reconnect
// modal before the user's next action fails, complementing the reactive path (a failed API call). Started after
// login, stopped on logout. The reachability check + interval are injectable so a test can drive one tick.
public sealed class SessionHeartbeat
{
    private readonly Action _onConnectionLost;
    private readonly Func<CancellationToken, Task<bool>> _check;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    public SessionHeartbeat(Action onConnectionLost, Func<CancellationToken, Task<bool>>? check = null, TimeSpan? interval = null)
    {
        _onConnectionLost = onConnectionLost;
        _check = check ?? (ct => ServerReachability.CheckAsync(DesktopClientOptions.ApiBaseUrl, ct));
        _interval = interval ?? TimeSpan.FromSeconds(15);
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await TickAsync(cancellationToken);
        }
    }

    // One heartbeat: probe the server and, if unreachable, raise the connection-lost callback. Exposed for tests.
    internal async Task TickAsync(CancellationToken cancellationToken = default)
    {
        bool reachable;
        try
        {
            reachable = await _check(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!reachable && !cancellationToken.IsCancellationRequested)
        {
            _onConnectionLost();
        }
    }
}
