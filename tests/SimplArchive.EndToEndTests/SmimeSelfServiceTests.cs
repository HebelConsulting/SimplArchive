using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit.Cryptography;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// Self-service IMAP encryption (#1332, ADR 0816): a user sets their own S/MIME certificate — uploaded or
// generated — and the core envelopes their IMAP fetches IN-PROCESS, no encryption sidecar involved. The
// per-test tenant deliberately has NO encryption mode, which is exactly the sidecar-free
// installation the feature exists for. The decisive test closes the full circle: the p12 the endpoint
// returned opens the message the IMAP surface served.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class SmimeSelfServiceTests
{
    private readonly E2EApiFactory _factory;

    public SmimeSelfServiceTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, string Email, string ImapPassword, string RepoName)> SeedUserWithDocumentAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Smime{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"smime-{Guid.NewGuid():N}@e2e.local";
        const string password = "smime-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Smime User");
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        using var dav = _factory.CreateClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{repoName}/note.eml")
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes(
                "From: Alice <alice@example.test>\r\nSubject: Plain secret\r\n"
                + "Date: Mon, 06 Jul 2026 10:00:00 +0000\r\n\r\nCLEARTEXT-BODY-MARKER\r\n")),
            Headers = { Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}"))) },
        };
        Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);

        return (api, email, imapPassword, repoName);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        // Read the body ONCE — probing a problem response consumes the stream.
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("errorCode").GetString()!;
    }

    [Fact]
    public async Task The_state_machine_holds_upload_generate_disable_until_delete()
    {
        var (api, email, _, _) = await SeedUserWithDocumentAsync();
        using var _api = api;

        // Fresh user: self-service on (unlisted tenant), nothing set.
        var status = await TestJson.Get(api, "/api/me/smime-certificate");
        Assert.True(status.GetProperty("selfService").GetBoolean());
        Assert.False(status.GetProperty("enabled").GetBoolean());

        // Generate: artifacts come back once, the p12 opens with the typed password, the state flips.
        var generated = await TestJson.Post(api, "/api/me/smime-certificate", new { p12Password = "typed-by-user" });
        Assert.True(generated.GetProperty("enabled").GetBoolean());
        Assert.Contains(email, generated.GetProperty("subject").GetString());
        var pkcs12 = Convert.FromBase64String(generated.GetProperty("pkcs12").GetString()!);
        var identity = X509CertificateLoader.LoadPkcs12(pkcs12, "typed-by-user");
        Assert.True(identity.HasPrivateKey);
        var mobileConfig = Encoding.UTF8.GetString(Convert.FromBase64String(generated.GetProperty("mobileConfig").GetString()!));
        Assert.Contains("com.apple.security.pkcs12", mobileConfig, StringComparison.Ordinal);

        // Set means SET: both entrances refuse until delete — the dialog's disabled buttons are a claim
        // the server must also make, or the rule is decoration.
        var againPost = await api.PostAsJsonAsync("/api/me/smime-certificate", new { p12Password = "x" });
        Assert.Equal(HttpStatusCode.Conflict, againPost.StatusCode);
        Assert.Equal("SMIME_CERTIFICATE_ALREADY_SET", await ErrorCodeAsync(againPost));
        var againPut = await api.PutAsync("/api/me/smime-certificate", new StringContent("irrelevant"));
        Assert.Equal(HttpStatusCode.Conflict, againPut.StatusCode);

        // Delete re-opens both.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync("/api/me/smime-certificate")).StatusCode);
        status = await TestJson.Get(api, "/api/me/smime-certificate");
        Assert.False(status.GetProperty("enabled").GetBoolean());

        // Upload the PUBLIC half of a locally generated identity — PEM in, status reflects it.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var uploaded = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var putResponse = await api.PutAsync("/api/me/smime-certificate",
            new StringContent(uploaded.ExportCertificatePem()));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // Private-key material is refused loudly — after a delete, so the guard hit is the CONTENT one.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync("/api/me/smime-certificate")).StatusCode);
        var keyUpload = await api.PutAsync("/api/me/smime-certificate",
            new StringContent(key.ExportRSAPrivateKeyPem()));
        Assert.Equal(HttpStatusCode.BadRequest, keyUpload.StatusCode);
        Assert.Equal("SMIME_CERTIFICATE_INVALID", await ErrorCodeAsync(keyUpload));
    }

    [Fact]
    public async Task The_generated_identity_opens_what_the_imap_surface_serves()
    {
        var (api, email, imapPassword, repoName) = await SeedUserWithDocumentAsync();
        using var _api = api;

        var generated = await TestJson.Post(api, "/api/me/smime-certificate", new { p12Password = "full-circle" });
        var pkcs12 = Convert.FromBase64String(generated.GetProperty("pkcs12").GetString()!);

        // A fresh IMAP connection (the certificate is cached at LOGIN) serves ciphertext...
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);
        var folder = await client.GetFolderAsync(repoName);
        await folder.OpenAsync(FolderAccess.ReadOnly);
        using var message = await folder.GetMessageAsync(0);

        Assert.IsType<ApplicationPkcs7Mime>(message.Body);
        using (var raw = new MemoryStream())
        {
            await message.WriteToAsync(raw);
            var text = Encoding.ASCII.GetString(raw.ToArray());
            Assert.Contains("application/pkcs7-mime", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CLEARTEXT-BODY-MARKER", text, StringComparison.Ordinal);
        }

        // ...and the p12 the endpoint handed out is what opens it — the whole feature in one assertion.
        using var context = new TemporarySecureMimeContext();
        await context.ImportAsync(new MemoryStream(pkcs12), "full-circle");
        var decrypted = ((ApplicationPkcs7Mime)message.Body).Decrypt(context);
        using var plain = new MemoryStream();
        await decrypted.WriteToAsync(plain);
        Assert.Contains("CLEARTEXT-BODY-MARKER", Encoding.ASCII.GetString(plain.ToArray()), StringComparison.Ordinal);

        await client.DisconnectAsync(quit: true);

        // Delete the certificate: the NEXT connection is plaintext again.
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync("/api/me/smime-certificate")).StatusCode);
        using var second = new ImapClient();
        await second.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await second.AuthenticateAsync(email, imapPassword);
        var reopened = await second.GetFolderAsync(repoName);
        await reopened.OpenAsync(FolderAccess.ReadOnly);
        using var plaintextMessage = await reopened.GetMessageAsync(0);
        using var plaintextRaw = new MemoryStream();
        await plaintextMessage.WriteToAsync(plaintextRaw);
        Assert.Contains("CLEARTEXT-BODY-MARKER", Encoding.ASCII.GetString(plaintextRaw.ToArray()), StringComparison.Ordinal);
        await second.DisconnectAsync(quit: true);
    }
}
