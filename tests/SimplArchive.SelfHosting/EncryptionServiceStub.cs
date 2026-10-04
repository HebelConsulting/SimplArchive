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
/// <b>No certificate registry.</b> The real service's <c>/api/users/{email}/certificate</c> and
/// <c>/enveloped</c> routes are retired (ADR 0890) and the core no longer calls them, so this stub does not
/// serve them: a reader's certificates come from the Module or the reader's own column.
/// </para>
/// </remarks>
public sealed class EncryptionServiceStub : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RSA _kek = RSA.Create(2048);
    private CancellationTokenSource? _stopping;

    /// <summary>Where the Api should point <c>Encryption:ServiceUrl</c>.</summary>
    public string Url { get; private set; } = string.Empty;

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

    /// <summary>
    /// The at-rest blob format both sides share — <c>nonce(12) ‖ ciphertext ‖ tag(16)</c>, AES-256-GCM.
    /// </summary>
    /// <remarks>
    /// Written out here rather than referenced from Infrastructure on purpose: this stub stands in for a
    /// SEPARATE product, and a stub that shared the core's own cipher helper would agree with it by
    /// construction — including about a mistake. The format is three lines; an independent reading of it is
    /// what makes the round trip mean something.
    /// </remarks>
    private static byte[] Decrypt(byte[] dek, byte[] blob)
    {
        const int nonce = 12;
        const int tag = 16;
        var plaintext = new byte[blob.Length - nonce - tag];
        using var aes = new AesGcm(dek, tag);
        aes.Decrypt(
            nonce: blob.AsSpan(0, nonce),
            ciphertext: blob.AsSpan(nonce, plaintext.Length),
            tag: blob.AsSpan(blob.Length - tag),
            plaintext: plaintext);
        return plaintext;
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

        if (path == "/api/decrypted-envelope" && context.Request.HttpMethod == "POST")
        {
            // DECRYPT-AND-ENVELOPE (core ADR 0862), done for real: unwrap, fetch the ciphertext from the
            // address the core signed, decrypt, and envelope to every certificate named.
            //
            // It has to be real, because the desktop suite's point is that the CLIENT opens what the server
            // served (ADR 0860) — a canned answer would prove the card can open a blob this stub invented.
            using var reader = new StreamReader(context.Request.InputStream);
            var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;

            var pems = body.GetProperty("recipientCertificatePems").EnumerateArray()
                .Select(p => p.GetString()!).ToList();
            if (pems.Count == 0)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            var dek = _kek.Decrypt(
                Convert.FromBase64String(body.GetProperty("wrappedDek").GetString()!),
                RSAEncryptionPadding.OaepSHA256);

            using var fetcher = new HttpClient();
            var blob = await fetcher.GetByteArrayAsync(body.GetProperty("ciphertextUrl").GetString()!);
            var plaintext = Decrypt(dek, blob);

            // The inner part, when named — the same assembly the real service performs (service ADR 0017).
            var contentType = body.TryGetProperty("innerContentType", out var ct) ? ct.GetString() : null;
            var disposition = body.TryGetProperty("innerContentDisposition", out var cd) ? cd.GetString() : null;
            if (contentType is { Length: > 0 } || disposition is { Length: > 0 })
            {
                var headers = new System.Text.StringBuilder();
                if (contentType is { Length: > 0 })
                {
                    headers.Append("Content-Type: ").Append(contentType).Append("\r\n");
                }

                if (disposition is { Length: > 0 })
                {
                    headers.Append("Content-Disposition: ").Append(disposition).Append("\r\n");
                }

                headers.Append("\r\n");
                plaintext = [.. System.Text.Encoding.ASCII.GetBytes(headers.ToString()), .. plaintext];
            }

            var envelope = new System.Security.Cryptography.Pkcs.EnvelopedCms(
                new System.Security.Cryptography.Pkcs.ContentInfo(plaintext));
            var recipients = new System.Security.Cryptography.Pkcs.CmsRecipientCollection();
            foreach (var pem in pems)
            {
                recipients.Add(new System.Security.Cryptography.Pkcs.CmsRecipient(
                    System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem)));
            }

            envelope.Encrypt(recipients);
            var encoded = envelope.Encode();

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/pkcs7-mime";
            await context.Response.OutputStream.WriteAsync(encoded);
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
