using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace SimplArchive.Api.Imap;

/// <summary>
/// The IMAP endpoint's listener (ADR "IMAP endpoint (read-only, first slice)") — a hosted service holding one
/// raw <see cref="TcpListener"/> per configured port (plaintext dev port and/or implicit-TLS port), spawning an
/// <see cref="ImapSession"/> per connection. A raw listener rather than a Kestrel transport because IMAP is not
/// HTTP — and because it keeps the endpoint alive under WebApplicationFactory, where the E2E suite drives it
/// with a real mail-client library.
/// </summary>
public sealed class ImapServer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ImapOptions> _options;
    private readonly ILogger<ImapServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<(TcpListener Listener, bool Tls)> _listeners = [];
    private readonly CancellationTokenSource _stopping = new();
    private X509Certificate2? _certificate;

    public ImapServer(IServiceScopeFactory scopeFactory, IOptions<ImapOptions> options, ILogger<ImapServer> logger, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The actually-bound ports (after ephemeral resolution) — what tests and the dialog surface read.</summary>
    public int? BoundPort { get; private set; }

    public int? BoundTlsPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return Task.CompletedTask;
        }

        if (options.TlsPort != 0)
        {
            if (options.CertificatePem is not { Length: > 0 } pem || options.CertificateKeyPem is not { Length: > 0 } key)
            {
                throw new InvalidOperationException("Imap:TlsPort is configured but Imap:CertificatePem/CertificateKeyPem are not (ADR 0339-style PEM sourcing).");
            }

            // Re-exported through PKCS#12 because the ephemeral-key PEM import is not usable for TLS on all OSes.
            using var fromPem = X509Certificate2.CreateFromPem(pem, key);
            _certificate = X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), null);
            Bind(options.TlsPort, tls: true);
            BoundTlsPort = ((IPEndPoint)_listeners[^1].Listener.LocalEndpoint).Port;
        }

        if (options.Port != 0)
        {
            Bind(options.Port, tls: false);
            BoundPort = ((IPEndPoint)_listeners[^1].Listener.LocalEndpoint).Port;
        }

        foreach (var (listener, tls) in _listeners)
        {
            _ = AcceptLoopAsync(listener, tls, _stopping.Token);
        }

        _logger.LogInformation("IMAP endpoint listening (plaintext: {Port}, TLS: {TlsPort})", BoundPort, BoundTlsPort);
        return Task.CompletedTask;
    }

    private void Bind(int configuredPort, bool tls)
    {
        // -1 = ephemeral: the OS picks a free port (the test fixture reads it back from BoundPort).
        var listener = new TcpListener(IPAddress.Any, configuredPort == -1 ? 0 : configuredPort);
        listener.Start();
        _listeners.Add((listener, tls));
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool tls, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // listener stopped
            }

            var session = new ImapSession(_scopeFactory, _loggerFactory.CreateLogger<ImapSession>(), tls ? _certificate : null);
            _ = session.RunAsync(client, stopping);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        foreach (var (listener, _) in _listeners)
        {
            listener.Stop();
        }

        return Task.CompletedTask;
    }
}
