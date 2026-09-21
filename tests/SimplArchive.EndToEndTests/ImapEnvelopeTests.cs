using System.Net.Http.Headers;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// The IMAP envelope hook (SimplArchiveEncryption ADR 0007): with an encryption service configured, a FETCH
// serves the message enveloped to the recipient's registered certificate; a recipient WITHOUT one gets
// plaintext — the POC's stated contract, not an error.
//
// The collection's factory hosts a STUB service whose default answer is 404 for everyone, so every other
// IMAP test in this suite keeps exercising the plaintext path merely by running. The stub returns a marker
// message rather than real CMS: the core treats the bytes as OPAQUE, so this proves the wiring end to end —
// funnel placement, recipient identity, the 404 contract — while the crypto itself is proven by the
// service's own cross-implementation tests in its repository.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImapEnvelopeTests
{
    private readonly E2EApiFactory _factory;

    public ImapEnvelopeTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(string Email, string ImapPassword, string RepoName)> SeedUserWithDocumentAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Env{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"envelope-{Guid.NewGuid():N}@e2e.local";
        const string password = "envelope-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Envelope User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        // File one .eml over WebDAV — the same byte path the sibling IMAP tests use.
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
        Assert.Equal(System.Net.HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);

        return (email, imapPassword, repoName);
    }

    private async Task<string> FetchRawMessageAsync(string email, string imapPassword, string repoName)
    {
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var folder = await client.GetFolderAsync(repoName);
        await folder.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(1, folder.Count);
        using var message = await folder.GetMessageAsync(0);
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream);
        await client.DisconnectAsync(quit: true);
        return Encoding.ASCII.GetString(stream.ToArray());
    }

    [Fact]
    public async Task A_registered_recipient_gets_the_enveloped_message()
    {
        var (email, imapPassword, repoName) = await SeedUserWithDocumentAsync();
        _factory.RegisterEncryptionRecipient(email);

        var raw = await FetchRawMessageAsync(email, imapPassword, repoName);

        // The stub's marker proves the served bytes came THROUGH the envelope hook — and nothing of the
        // plaintext may remain, because a hook that enveloped the body while some other FETCH view leaked
        // the original is exactly the two-views-disagreeing family the funnel placement exists to prevent.
        Assert.Contains(E2EApiFactory.EnvelopedMarkerHeader, raw, StringComparison.Ordinal);
        Assert.Contains("application/pkcs7-mime", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("CLEARTEXT-BODY-MARKER", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain secret", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recipient_without_a_certificate_gets_plaintext()
    {
        var (email, imapPassword, repoName) = await SeedUserWithDocumentAsync();
        // Deliberately NOT registered — the stub answers 404, which is the contract, not a failure.

        var raw = await FetchRawMessageAsync(email, imapPassword, repoName);

        Assert.Contains("CLEARTEXT-BODY-MARKER", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(E2EApiFactory.EnvelopedMarkerHeader, raw, StringComparison.Ordinal);
    }
}
