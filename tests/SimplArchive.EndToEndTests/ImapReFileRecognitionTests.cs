using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// A document read over IMAP, exported, and filed back must not become a silent duplicate (#782). A non-.eml
// document is served over IMAP as a synthetic mail whose Message-ID we mint as `<{documentId}@simplarchive>` —
// self-identifying. So a re-filed export is recognisable as OURS, and the interactive filing probe
// (GET /api/duplicates, the call the reference/copy/cancel modal is built on) resolves the exact original and
// offers it, instead of the archive growing a mail-shaped clone nothing can dedupe.
//
// The trap this also pins (#782): the id rides in a header a user can edit and carry between tenants, so it is
// a HINT that must be re-verified — a `<{random-guid}@simplarchive>` naming no visible document resolves to
// nothing, exactly as if it named nothing.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ImapReFileRecognitionTests
{
    private readonly E2EApiFactory _factory;

    public ImapReFileRecognitionTests(E2EApiFactory factory) => _factory = factory;

    private sealed record World(string Email, string Password, string ImapPassword, string RepoName, Guid RepoId, int Port);

    private async Task<(World World, Guid DocumentId, string DocumentName)> SeedDocumentAsync(string fileName)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"ImapRF{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();

        var email = $"imap-rf-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-rf-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Refile User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        var davGen = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var basic = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davGen.GetProperty("password").GetString()}")));

        // A plain PDF — a NON-.eml document, so IMAP serves it as a synthetic wrapper (the case #782 fixes).
        var bytes = new byte[2048];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(bytes, 0);
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/SimplArchive/{repoName}/{fileName}")
        {
            Content = new ByteArrayContent(bytes),
            Headers = { Authorization = basic },
        })
        {
            using var dav = _factory.CreateClient();
            (await dav.SendAsync(put)).EnsureSuccessStatusCode();
        }

        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await api.PutAsJsonAsync("/api/me/imap-access/settings", new { showAllDocuments = true })).StatusCode);

        // The document's id, read INDEPENDENTLY of the Message-ID under test — so asserting the probe returns
        // this id proves recognition resolved the right document, not merely echoed the guid it was handed.
        var children = await TestJson.Get(api, $"/api/documents/{repoId}/children");
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var doc = children.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == stem);
        var documentId = doc.GetProperty("id").GetGuid();

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        return (new World(email, password, imapPassword, repoName, repoId, port), documentId, stem);
    }

    // Fetch the synthetic wrapper's Message-ID over IMAP — the exact header a mail client would carry into an
    // exported .eml.
    private static async Task<string> FetchMessageIdAsync(World world)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", world.Port);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        await reader.ReadLineAsync(); // greeting

        async Task<string> SendAsync(string tag, string command)
        {
            await writer.WriteLineAsync($"{tag} {command}");
            var sb = new StringBuilder();
            for (var i = 0; i < 200; i++)
            {
                var line = await reader.ReadLineAsync();
                if (line is null || line.StartsWith(tag + " ", StringComparison.Ordinal))
                {
                    break;
                }

                sb.Append(line).Append('\n');
            }

            return sb.ToString();
        }

        await SendAsync("a1", $"LOGIN \"{world.Email}\" \"{world.ImapPassword}\"");
        await SendAsync("a2", $"SELECT \"{world.RepoName}\"");
        var response = await SendAsync("a3", "FETCH 1 (BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])");

        var match = Regex.Match(response, "Message-ID:\\s*(<[^>]+>)", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"no Message-ID in the fetched header: {response}");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task A_synthetic_wrapper_read_over_imap_is_recognised_when_filed_back()
    {
        var (world, documentId, documentName) = await SeedDocumentAsync("refile-me.pdf");

        // It really is OUR self-identifying id, naming the exact document (`<{documentId}@simplarchive>`).
        var messageId = await FetchMessageIdAsync(world);
        var embedded = Regex.Match(messageId, "<([0-9a-fA-F-]+)@simplarchive>");
        Assert.True(embedded.Success, $"the wrapper's Message-ID is not one of ours: {messageId}");
        Assert.Equal(documentId, Guid.Parse(embedded.Groups[1].Value));

        // Filing it back interactively: the probe the reference/copy/cancel modal is built on. It recognises the
        // wrapper and returns the ORIGINAL — which the archive holds as a PDF, not an eMail, so the old
        // mask-scoped Entry-ID probe could never have found it.
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(world.Email, world.Password));
        var probe = await TestJson.Get(api, $"/api/duplicates?entryId={Uri.EscapeDataString(messageId)}");
        var duplicates = probe.GetProperty("duplicates").EnumerateArray().ToList();
        Assert.Contains(duplicates, d => d.GetProperty("id").GetGuid() == documentId);
        Assert.Contains(duplicates, d => d.GetProperty("name").GetString() == documentName);
    }

    [Fact]
    public async Task A_synthetic_id_naming_no_visible_document_resolves_to_nothing()
    {
        // The trap (#782): the id is an untrusted, externally-editable hint. A well-formed synthetic id that
        // names a document this caller cannot see — here, one that simply does not exist — must behave exactly
        // as if it named nothing, never as a probe for whether some document exists.
        var (world, _, _) = await SeedDocumentAsync("real.pdf");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(world.Email, world.Password));

        var forged = $"<{Guid.NewGuid()}@simplarchive>";
        var probe = await TestJson.Get(api, $"/api/duplicates?entryId={Uri.EscapeDataString(forged)}");
        Assert.Empty(probe.GetProperty("duplicates").EnumerateArray());
    }
}
