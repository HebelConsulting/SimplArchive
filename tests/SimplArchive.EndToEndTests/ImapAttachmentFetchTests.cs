using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// Downloading an ATTACHMENT over IMAP (issue #766, reported from real use: a mail client "cannot open or
// correctly download (corrupted file) the pdf-files shown in the IMAP configured folders").
//
// The existing endpoint test fetches whole messages — BODY[] — and that path works. A mail client asking for
// just the attachment asks for a NUMBERED SECTION, BODY[2], and that is a different code path with a different
// answer. So this drives the same real client library down the path a user's client actually takes, and
// compares the bytes it gets against the bytes that were filed.
[Collection(E2ECollection.Name)]
public class ImapAttachmentFetchTests
{
    private readonly E2EApiFactory _factory;

    public ImapAttachmentFetchTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Fetching_only_the_attachment_returns_the_document_byte_for_byte()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Imap{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"imap-att-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-att-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Imap Attachment User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        var davGen = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var basic = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davGen.GetProperty("password").GetString()}")));

        // Big enough and binary enough that a wrong answer cannot coincidentally match: every byte value, many
        // times over, with a PDF header so the file is what it claims to be. A few ASCII bytes would let a
        // truncation or an off-by-a-header pass unnoticed.
        var pdf = new byte[8192];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(pdf, 0);
        for (var i = 9; i < pdf.Length; i++)
        {
            pdf[i] = (byte)(i * 31 % 256);
        }

        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/webdav/{repoName}/statement.pdf")
        {
            Content = new ByteArrayContent(pdf),
            Headers = { Authorization = basic },
        })
        {
            using var dav = _factory.CreateClient();
            Assert.Equal(System.Net.HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        }

        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var repo = await client.GetFolderAsync(repoName);
        await repo.OpenAsync(FolderAccess.ReadOnly);

        var summary = (await repo.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure))
            .Single(s => s.Body is not null);
        var attachment = Assert.Single(summary.BodyParts.OfType<BodyPartBasic>(), p => p.FileName == "statement.pdf");

        // The client's own attachment download: BODY[<section>] for that part alone, never the whole message.
        var part = Assert.IsAssignableFrom<MimeKit.MimePart>(await repo.GetBodyPartAsync(summary.UniqueId, attachment));
        using var decoded = new MemoryStream();
        Assert.NotNull(part.Content);
        part.Content.DecodeTo(decoded);

        Assert.Equal(pdf, decoded.ToArray());

        var uid = (int)summary.UniqueId.Id;
        await client.DisconnectAsync(true);

        await AssertTwoLiteralsAreSeparatedAsync(port, email, imapPassword, repoName, uid);
    }

    // A document REFERENCED into a folder is another appearance of that document, so it belongs in the folder's
    // message list exactly as a child does. The mailbox walk has taken referenced FOLDERS since #596; documents
    // were left out, so a mail client listed a folder's children and silently omitted everything a user had
    // filed there by reference — reported as "only PDFs are shown, not document links" (#766).
    [Fact]
    public async Task A_referenced_document_appears_as_a_message_and_an_unreadable_one_does_not()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"ImapRef{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();
        var sourceName = $"ImapSrc{Guid.NewGuid():N}"[..12];
        var sourceId = (await TestJson.Post(owner, "/api/repositories", new { name = sourceName })).GetProperty("id").GetGuid();

        var email = $"imap-ref-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-ref-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Imap Reference User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        // A document living in the OTHER repository, then referenced into this one — the shape a user makes
        // when they file something into a working folder without moving it.
        var docId = await CreateDocumentAsync(owner, sourceId, "referenced-report");
        await TestJson.Post(owner, $"/api/documents/{repoId}/references", new { targetId = docId });

        // A plain child alongside it. It is what makes the withholding below MEAN something: a user who sees
        // this one and not the referenced one is being filtered, while a user who sees neither might simply
        // have an empty or unreachable mailbox — which would satisfy a DoesNotContain on its own.
        await CreateDocumentAsync(owner, repoId, "own-child");

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var repo = await client.GetFolderAsync(repoName);
        await repo.OpenAsync(FolderAccess.ReadOnly);
        var subjects = (await repo.FetchAsync(0, -1, MessageSummaryItems.Envelope)).Select(s => s.Envelope!.Subject).ToList();

        Assert.Contains("referenced-report.txt", subjects);
        Assert.Contains("own-child.txt", subjects);
        await client.DisconnectAsync(true);

        // …and a user who may see the folder but NOT the reference's target does not get it. A referenced
        // document lives elsewhere, so its rights are its own — inherited from its real parent, not from the
        // folder it is referenced into. Without the check, projecting references would hand a mail client a
        // document nobody shared with its owner. The same shape as the referenced-FOLDER case in
        // ImapReferenceMailboxTests, for the same reason.
        var otherEmail = $"imap-ref-other-{Guid.NewGuid():N}@e2e.local";
        const string otherPassword = "imap-ref-o-1234";
        var otherId = await _factory.SeedUserAsync(tenantId, otherEmail, otherPassword, "Other Reference User");
        (await owner.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{otherId}",
            new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        using var otherApi = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(otherEmail, otherPassword));
        var otherImapPassword = (await TestJson.Post(otherApi, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await otherApi.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        using var otherClient = new ImapClient();
        await otherClient.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await otherClient.AuthenticateAsync(otherEmail, otherImapPassword);
        var otherRepo = await otherClient.GetFolderAsync(repoName);
        await otherRepo.OpenAsync(FolderAccess.ReadOnly);
        var otherSubjects = (await otherRepo.FetchAsync(0, -1, MessageSummaryItems.Envelope)).Select(s => s.Envelope!.Subject).ToList();

        Assert.Contains("own-child.txt", otherSubjects);
        Assert.DoesNotContain("referenced-report.txt", otherSubjects);
        await otherClient.DisconnectAsync(true);
    }

    private static async Task<Guid> CreateDocumentAsync(HttpClient http, Guid parentId, string name)
    {
        var docId = (await TestJson.Post(http, $"/api/documents/{parentId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(http, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.ASCII.GetBytes("referenced content")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return docId;
    }

    // TWO literal sections in ONE fetch, read off the wire.
    //
    // Driven raw rather than through the client library, because the library would not ask for it: MailKit
    // fetches at most one literal per command, so a test written through it passes with the defect present —
    // measured, not assumed. The wire is the only place this shows.
    //
    // A FETCH response is ONE line with binary spliced into it, so the data item after a literal's octets still
    // needs its separating SPACE. Without it the wire carried `<octets>BODY[TEXT] {45}` with nothing between,
    // which a strict parser reads as a single malformed atom.
    private static async Task AssertTwoLiteralsAreSeparatedAsync(int port, string email, string password, string mailbox, int uid)
    {
        using var raw = new TcpClient();
        await raw.ConnectAsync("127.0.0.1", port);
        await using var stream = raw.GetStream();
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        async Task<byte[]> ReadUntilAsync(string tag)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (!Encoding.Latin1.GetString(buffer.ToArray()).Contains($"\r\n{tag} ", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(chunk).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        await writer.WriteLineAsync($"a1 LOGIN {email} {password}");
        await ReadUntilAsync("a1");
        await writer.WriteLineAsync($"a2 SELECT \"{mailbox}\"");
        await ReadUntilAsync("a2");
        await writer.WriteLineAsync($"a3 UID FETCH {uid} (BODY.PEEK[HEADER] BODY.PEEK[TEXT])");
        var response = await ReadUntilAsync("a3");

        // Find the first literal's announced size, skip exactly that many octets, and look at what follows.
        var text = Encoding.Latin1.GetString(response);
        var marker = text.IndexOf("BODY[HEADER] {", StringComparison.Ordinal);
        Assert.True(marker >= 0, $"no BODY[HEADER] literal in the response: {text[..Math.Min(400, text.Length)]}");
        var sizeStart = marker + "BODY[HEADER] {".Length;
        var size = int.Parse(text[sizeStart..text.IndexOf('}', sizeStart)]);
        var octetsStart = text.IndexOf("\r\n", sizeStart, StringComparison.Ordinal) + 2;

        var after = text[(octetsStart + size)..];
        Assert.StartsWith(" BODY[TEXT] {", after);
    }
}
