using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace SimplArchive.Api.Lmtp;

/// <summary>
/// The LMTP delivery listener (ADR 0628) — a hosted service holding one <see cref="TcpListener"/> and spawning
/// an <see cref="LmtpSession"/> per connection, the same shape as <c>ImapServer</c>.
/// </summary>
/// <remarks>
/// A raw listener rather than a Kestrel transport for the same reasons IMAP uses one: LMTP is not HTTP, and it
/// keeps the endpoint alive under <c>WebApplicationFactory</c> so the E2E suite can deliver a real message.
/// </remarks>
public sealed class LmtpServer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<LmtpOptions> _options;
    private readonly ILogger<LmtpServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;

    public LmtpServer(IServiceScopeFactory scopeFactory, IOptions<LmtpOptions> options, ILogger<LmtpServer> logger, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The actually-bound port after ephemeral resolution — what the E2E suite reads.</summary>
    public int? BoundPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return Task.CompletedTask;
        }

        // Loopback, not Any. The listener is unauthenticated by design (see LmtpOptions), so the default bind
        // must be the one that cannot be reached from off the host; a deployment that needs the MTA in another
        // container overrides it deliberately rather than inheriting an open port.
        // -1 means "pick an ephemeral port", the same convention ImapServer uses so a test run never
        // collides with a real one.
        _listener = new TcpListener(IPAddress.Loopback, options.Port == -1 ? 0 : options.Port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _logger.LogInformation("LMTP delivery listener bound on {Port}", BoundPort);
        _ = Task.Run(() => AcceptLoopAsync(_listener, _stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        _listener?.Stop();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var session = new LmtpSession(
                        client, _scopeFactory, _options.Value, _loggerFactory.CreateLogger("SimplArchive.Api.Lmtp"));
                    try
                    {
                        await session.RunAsync(cancellationToken);
                    }
                    catch (Exception e)
                    {
                        // One malformed conversation must not take the listener down with it. The MTA will
                        // retry, because a dropped connection is a temporary failure to it.
                        _logger.LogError(e, "LMTP session ended with an unhandled error");
                    }
                }
            }, CancellationToken.None);
        }
    }
}
