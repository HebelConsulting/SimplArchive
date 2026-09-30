using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// The IMAP envelope hook (SimplArchiveEncryptionService ADR 0007) and its per-tenant gate (ADR 0813): with an
// encryption service configured, a FETCH serves the message enveloped to the recipient's registered
// certificate — but only for a tenant Encryption:Tenants lists. The factory lists exactly the seeded
// CryptoDemo tenant (the kiosk's shape), so the seeded tenant carries the positive path and every
// per-test tenant is the unlisted negative one; a recipient WITHOUT a certificate gets plaintext either
// way — the POC's stated contract, not an error.
//
// The collection's factory hosts a STUB service whose default answer is 404 for everyone, so every other
// IMAP test in this suite keeps exercising the plaintext path merely by running. The stub returns a marker
// message rather than real CMS: the core treats the bytes as OPAQUE, so this proves the wiring end to end —
// funnel placement, recipient identity, the tenant gate, the 404 contract — while the crypto itself is
// proven by the service's own cross-implementation tests in its repository.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImapEnvelopeTests
{
    private readonly E2EApiFactory _factory;

    public ImapEnvelopeTests(E2EApiFactory factory) => _factory = factory;

    // The CryptoDemo seed derives florian/thomas from the admin's domain (ADR 0813).
    private static string CryptoUserEmail(string localPart) =>
        $"{localPart}@{E2EApiFactory.CryptoAdminEmail.Split('@')[1]}";

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

    /// <summary>
    /// A reader on the mail-enveloping tenant, with IMAP access and one document to fetch.
    /// </summary>
    /// <remarks>
    /// The tenant is created here under the factory's declared name, which is the pattern that name exists
    /// for — a MODE is read from configuration at startup, so it must be declared there, while the tenant
    /// itself belongs to whichever test wants it. Several suites create `StrictTier`; each brings its own
    /// users and documents, so they share only the name and the mode.
    /// </remarks>
    private async Task<(string Email, string ImapPassword, string Folder)> MailEnvelopingReaderAsync()
    {
        var tenantId = await _factory.SeedTenantNamedAsync(E2EApiFactory.StrictTenantName);
        var email = $"imap-envelope-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "ie-1234", "Envelope Reader", canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "ie-1234"));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { }))
            .GetProperty("password").GetString()!;

        // SHOW ALL DOCUMENTS, or the mailbox is empty and this test fails for a reason that has nothing to
        // do with envelopes. IMAP lists only e-mail documents by default (#562); everything else appears as
        // a synthetic message carrying the file as an attachment, and it is that synthetic message the
        // envelope hook wraps. The CryptoDemo seed this test used to borrow had already opted in, so moving
        // off it made the choice this test's own — measured, not guessed: without it the fetch found 0
        // messages in a folder that certainly had a document.
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        // A repository becomes a mailbox (ADR 0509's tree mirroring), so its NAME is the folder the client
        // opens. Unique per run: the suite shares one installation, and a fixed name would make two runs of
        // this test fetch each other's message.
        var folder = $"EnvelopeMail-{Guid.NewGuid():N}"[..24];
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = folder }))
            .GetProperty("id").GetGuid();

        // Content, not just a document row: the plaintext synthetic message carries the document as an
        // attachment part, and the negative assertion below is that none of it survives the envelope. A
        // contentless document would make that assertion pass for the wrong reason.
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "sealed.txt" }))
            .GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("the strict tenant's mail body")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        return (email, imapPassword, folder);
    }

    private async Task<string> FetchRawMessageAsync(string email, string imapPassword, string folderName)
    {
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var folder = await client.GetFolderAsync(folderName);
        await folder.OpenAsync(FolderAccess.ReadOnly);
        Assert.True(folder.Count >= 1, $"expected at least one message in {folderName}, found {folder.Count}");
        using var message = await folder.GetMessageAsync(0);
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream);
        await client.DisconnectAsync(quit: true);
        return Encoding.ASCII.GetString(stream.ToArray());
    }

    [Fact]
    public async Task A_registered_recipient_on_a_mail_enveloping_tenant_gets_the_enveloped_message()
    {
        // MOVED OFF THE `Storage` TENANT BY #1414 (ADR 0858). This used to run as florian on the seeded
        // CryptoDemo tenant — `Storage` — because that was a mode which enveloped mail. It no longer is:
        // enveloping mail is delivery, and `Storage` protects content at rest and nothing more. So the
        // positive path moves to a tenant whose mode still envelopes mail.
        //
        // `StrictTier` rather than a third tenant name: it is already declared `Strict` in the factory for
        // the strict-tier suites, `Strict` envelopes mail, and its shut plaintext doors are irrelevant here —
        // IMAP is its own funnel, not one of those doors. Changing the CryptoDemo tenant's mode instead was
        // measured and rejected: `AtRestEncryptionTests` asserts plaintext WebDAV reads and an
        // `/api/encrypted-content` door on that tenant, and `Strict` refuses both.
        //
        // And this test SEEDS WHAT IT NEEDS rather than leaning on a shared seed, which is the house rule for
        // anything that mutates — the CryptoDemo arrangement it used to borrow was somebody else's fixture.
        var (email, imapPassword, folder) = await MailEnvelopingReaderAsync();
        _factory.RegisterEncryptionRecipient(email);

        var raw = await FetchRawMessageAsync(email, imapPassword, folder);

        // The stub's marker proves the served bytes came THROUGH the envelope hook — and nothing of the
        // plaintext may remain, because a hook that enveloped the body while some other FETCH view leaked
        // the original is exactly the two-views-disagreeing family the funnel placement exists to prevent.
        Assert.Contains(E2EApiFactory.EnvelopedMarkerHeader, raw, StringComparison.Ordinal);
        Assert.Contains("application/pkcs7-mime", raw, StringComparison.Ordinal);
        // The plaintext synthetic message carries the document as an application/pdf attachment part —
        // none of it may survive the envelope.
        Assert.DoesNotContain("application/pdf", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_recipient_in_an_unlisted_tenant_still_gets_plaintext()
    {
        // The per-tenant gate (ADR 0813): this test's own tenant is not in Encryption:Tenants, so even a
        // REGISTERED recipient is served plaintext without the service being asked — the same isolation
        // that keeps the kiosk's public demo tenant untouched beside the encrypted one.
        var (email, imapPassword, repoName) = await SeedUserWithDocumentAsync();
        _factory.RegisterEncryptionRecipient(email);

        var raw = await FetchRawMessageAsync(email, imapPassword, repoName);

        Assert.Contains("CLEARTEXT-BODY-MARKER", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(E2EApiFactory.EnvelopedMarkerHeader, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recipient_without_a_certificate_gets_plaintext_in_the_listed_tenant()
    {
        // thomas is in the LISTED tenant but deliberately NOT registered — the stub answers 404, which is
        // the contract, not a failure. Running him through the listed tenant is what keeps this test
        // meaningful: an unlisted tenant would be served plaintext for the wrong reason.
        var email = CryptoUserEmail("thomas");

        var raw = await FetchRawMessageAsync(email, E2EApiFactory.CryptoPassword, E2EApiFactory.CryptoTenantName);

        Assert.DoesNotContain(E2EApiFactory.EnvelopedMarkerHeader, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("application/pkcs7-mime", raw, StringComparison.Ordinal);
        // Positively plaintext: the synthetic message serves the document as a PDF attachment part.
        Assert.Contains("application/pdf", raw, StringComparison.Ordinal);
    }
}
