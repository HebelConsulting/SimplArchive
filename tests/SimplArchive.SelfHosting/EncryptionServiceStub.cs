using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimplArchive.SelfHosting;

/// <summary>
/// A stub of the per-installation encryption service, for a self-hosted app that needs an encrypting tenant
/// (core #1421).
/// </summary>
/// <remarks>
/// <para>
/// <b>OPT-IN, and that is a requirement rather than a courtesy.</b> <see cref="SelfHostedApp"/> is shared by
/// the desktop suite, the web-UI suite and the manual-capture harness (ADR 0502). Setting
/// <c>Encryption:ServiceUrl</c> swaps the object-storage client for its encrypting decorator for the whole
/// installation, so switching it on unconditionally would change what every one of those runs against. It is
/// off unless a caller asks.
/// </para>
/// <para>
/// <b>An <see cref="HttpListener"/> rather than a <c>WebApplication</c></b>, deliberately: this library has
/// no ASP.NET Core reference and its own comment values staying light so the desktop suite that wraps it
/// stays light. Three endpoints do not justify a framework reference. (The E2E factory hosts a richer stub
/// in-process — it also exercises KEK rotation, rewrapping and retirement, which nothing here needs. Two
/// stubs is a deliberate split, not an oversight: merging them would mean giving this library the framework
/// reference it is avoiding, to gain endpoints no consumer of it calls.)
/// </para>
/// <para>
/// <b>The KEK is real RSA and the wrap/unwrap really round-trips.</b> A stub that returned a fixed DEK would
/// let a test pass while the at-rest path was broken — and at-rest wrapping is what a <c>Strict</c> tenant
/// does on every write, so it has to work for the read under test to mean anything.
/// </para>
/// <para>
/// <b>The certificate route answers 404 until a caller registers one</b>, which keeps the shipped contract
/// (no certificate → the core refuses rather than serving plaintext) exercised by default.
/// </para>
/// </remarks>
public sealed class EncryptionServiceStub : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RSA _kek = RSA.Create(2048);
    private readonly Dictionary<string, string> _certificates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stopping;

    /// <summary>Where the Api should point <c>Encryption:ServiceUrl</c>.</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>Registers a reader's certificate, as the real service's provisioning door would.</summary>
    public void RegisterCertificate(string email, string certificatePem)
    {
        lock (_certificates)
        {
            _certificates[email] = certificatePem;
        }
    }

    public void Start()
    {
        // A port the OS chose, because the suites run in parallel CI jobs on one host and a fixed port is a
        // collision waiting for the second job.
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{Url}/");
        _listener.Start();
        _stopping = new CancellationTokenSource();
        _ = Task.Run(() => ServeAsync(_stopping.Token));
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
            {
                return; // shutting down — GetContextAsync throws once the listener closes
            }

            try
            {
                await RespondAsync(context);
            }
            catch (Exception)
            {
                // A stub that throws must not take the listener down with it: the Api would then see a
                // connection refused and report an unreachable service, which reads as a configuration
                // problem rather than a bug in this file.
                TrySetStatus(context, HttpStatusCode.InternalServerError);
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;

        if (path == "/api/kek/current" && context.Request.HttpMethod == "GET")
        {
            // oaepHash is published by the service because BOTH sides must agree (service ADR 0011 — the dev
            // HSM is SHA-1 only, and a hash we assumed produced an unwrappable DEK). SHA256 here: this stub
            // is software RSA, so it can do the stronger one, and saying so exercises the non-default branch.
            await WriteJsonAsync(context, new
            {
                generation = "stub-g1",
                publicKeyPem = _kek.ExportRSAPublicKeyPem(),
                oaepHash = "SHA256",
            });
            return;
        }

        if (path == "/api/unwrapped-dek" && context.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
            var wrapped = Convert.FromBase64String(body.GetProperty("wrappedDek").GetString()!);

            // Raw bytes, not JSON: the core reads the response body as the DEK itself.
            var dek = _kek.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/octet-stream";
            await context.Response.OutputStream.WriteAsync(dek);
            return;
        }

        if (path.StartsWith("/api/users/", StringComparison.Ordinal) && path.EndsWith("/certificate", StringComparison.Ordinal))
        {
            var email = Uri.UnescapeDataString(path["/api/users/".Length..^"/certificate".Length]);

            if (context.Request.HttpMethod == "PUT")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                RegisterCertificate(email, await reader.ReadToEndAsync());
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            string? pem;
            lock (_certificates)
            {
                _certificates.TryGetValue(email, out pem);
            }

            if (pem is null)
            {
                // THE DEFAULT ANSWER, and it is load-bearing: no certificate means the core refuses rather
                // than serving plaintext, so every run of a suite that switches this stub on exercises that
                // contract merely by not registering anything.
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // THE RAW PEM AS THE BODY, not JSON — `MessageEnvelopeClient` reads the response with
            // `ReadAsStringAsync` and treats the whole body as the certificate. A JSON envelope here is
            // accepted by the HTTP layer and then fails to parse as a certificate, so the core concludes the
            // reader has none: the download rel disappears and the read refuses, with nothing naming the
            // cause. Measured — the first version of this stub returned `{"certificatePem": …}`.
            var bytes = Encoding.UTF8.GetBytes(pem);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/x-pem-file";
            await context.Response.OutputStream.WriteAsync(bytes);
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static void TrySetStatus(HttpListenerContext context, HttpStatusCode status)
    {
        try
        {
            context.Response.StatusCode = (int)status;
        }
        catch (Exception)
        {
            // The response may already be committed; nothing useful left to do.
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        _stopping?.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        _kek.Dispose();
        _stopping?.Dispose();
        return ValueTask.CompletedTask;
    }
}
