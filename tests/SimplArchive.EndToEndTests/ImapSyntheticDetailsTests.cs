using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// A NON-email document is served over IMAP as a synthetic message, and its body used to be one line: the file
// name and the archive URL. Everything the clients' detail pane shows -- who filed it, when, which version,
// the mask and its index data -- was absent, so a mail client showed a document with no context at all.
//
// This drives a real client library and reads the message it actually receives, rather than asserting against
// the builder: the point is what a mail client renders, and the body is also what SEARCH scans, so a body that
// is right in principle and wrong on the wire would be invisible to a unit test of the formatter.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ImapSyntheticDetailsTests
{
    private readonly E2EApiFactory _factory;

    public ImapSyntheticDetailsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_synthetic_message_carries_the_detail_pane_in_its_body_and_its_headers()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"ImapD{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"imap-det-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-det-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Detail Pane User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var maskName = $"Invoice {Guid.NewGuid():N}"[..14];
        var mask = await TestJson.Post(api, "/api/masks", new
        {
            name = maskName,
            fields = new object[] { new { name = "Supplier", dataType = 0, isRequired = false } },
        });
        var maskId = mask.GetProperty("id").GetGuid();
        var fieldId = mask.GetProperty("fields").EnumerateArray().Single().GetProperty("id").GetGuid();

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        var davGen = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var basic = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davGen.GetProperty("password").GetString()}")));

        var pdf = new byte[4096];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(pdf, 0);
        for (var i = 9; i < pdf.Length; i++)
        {
            pdf[i] = (byte)(i * 17 % 256);
        }

        // Filed by the USER over WebDAV, so "Created by" has a real name to report rather than the service
        // account that made the repository.
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{repoName}/invoice.pdf")
        {
            Content = new ByteArrayContent(pdf),
            Headers = { Authorization = basic },
        })
        {
            using var dav = _factory.CreateClient();
            Assert.Equal(System.Net.HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        }

        var children = await TestJson.Get(api, $"/api/documents/{repoId}/children");
        var docId = children.GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "invoice").GetProperty("id").GetGuid();

        (await api.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();
        await TestJson.Put(api, $"/api/documents/{docId}/index-data",
            new { fields = new[] { new { fieldDefinitionId = fieldId, values = new[] { "Contoso AG" } } } });

        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var repo = await client.GetFolderAsync(repoName);
        await repo.OpenAsync(FolderAccess.ReadOnly);
        var summary = (await repo.FetchAsync(0, -1, MessageSummaryItems.UniqueId)).Single();
        var message = await repo.GetMessageAsync(summary.UniqueId);

        var body = Assert.IsAssignableFrom<MimeKit.TextPart>(message.BodyParts.First()).Text;

        // The LAYOUT, not just the words. Substring assertions cannot see arrangement: every one of them below
        // passes just as happily on a body whose blank lines are gone and whose blocks have run together, which
        // is precisely how this was nearly signed off. So the shape is pinned first.
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Equal("invoice.pdf", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.StartsWith("Filed", lines[2], StringComparison.Ordinal);

        // A blank line separates the system rows from the mask block, and the mask block from the signature.
        var blanks = lines.Select((l, i) => (l, i)).Where(x => x.l.Length == 0).Select(x => x.i).ToList();
        Assert.Equal(3, blanks.Count);
        Assert.Equal("Served from the SimplArchive archive:", lines[blanks[2] + 1]);

        // Values line up in a column rather than being jammed against their labels.
        var filedRow = lines.Single(l => l.StartsWith("Filed", StringComparison.Ordinal));
        Assert.Matches(@"^Filed {2,}\d", filedRow);

        // The system fields the detail pane shows.
        Assert.Contains("Filed", body, StringComparison.Ordinal);
        Assert.Contains("Document date", body, StringComparison.Ordinal);
        Assert.Contains("Detail Pane User", body, StringComparison.Ordinal);
        Assert.Contains("Version", body, StringComparison.Ordinal);

        // The mask and its index data -- the half a user actually recognises a document by.
        Assert.Contains(maskName, body, StringComparison.Ordinal);
        Assert.Contains("Supplier", body, StringComparison.Ordinal);
        Assert.Contains("Contoso AG", body, StringComparison.Ordinal);

        // The signature stays, and the attachment stays in BODY[2] -- moving it is the defect class #766 was.
        Assert.Contains("https://www.simplarchive.dev", body, StringComparison.Ordinal);
        Assert.Single(message.Attachments);

        // The same values as headers a client can filter on.
        Assert.Equal("Detail Pane User", message.Headers["X-SimplArchive-Createdby"]);
        Assert.Equal(maskName, message.Headers["X-SimplArchive-Mask"]);
        Assert.Equal("Contoso AG", message.Headers["X-SimplArchive-Field-Supplier"]);

        await client.DisconnectAsync(true);
    }

    // A real .eml is returned byte-for-byte and must NOT gain any of this: rewriting a message a user filed is
    // the "serving something else" failure, and a client would see a different message than was archived.
    [Fact]
    public async Task A_real_email_is_untouched()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"ImapE{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"imap-eml-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-eml-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Eml User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        var davGen = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var basic = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davGen.GetProperty("password").GetString()}")));

        const string raw = "From: someone@example.com\r\nTo: nobody@example.com\r\nSubject: Untouched\r\n"
            + "Message-Id: <original@example.com>\r\nDate: Mon, 02 Mar 2026 10:00:00 +0000\r\n\r\nJust the body.\r\n";

        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{repoName}/kept.eml")
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes(raw)),
            Headers = { Authorization = basic },
        })
        {
            using var dav = _factory.CreateClient();
            Assert.Equal(System.Net.HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);
        }

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        var repo = await client.GetFolderAsync(repoName);
        await repo.OpenAsync(FolderAccess.ReadOnly);
        var summary = (await repo.FetchAsync(0, -1, MessageSummaryItems.UniqueId)).Single();
        var message = await repo.GetMessageAsync(summary.UniqueId);

        Assert.Equal("<original@example.com>", message.MessageId is null ? null : $"<{message.MessageId}>");
        Assert.DoesNotContain(message.Headers, h => h.Field.StartsWith("X-SimplArchive-", StringComparison.OrdinalIgnoreCase));

        await client.DisconnectAsync(true);
    }
}
