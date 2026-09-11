using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// BODYSTRUCTURE is how a mail client decides a part is an ATTACHMENT rather than content to render, and this
// server answered it with the non-extensible BODY form: no disposition, no extension data at all. Apple Mail
// therefore rendered the base64 of a PDF as the message text.
//
// The message itself was well-formed the whole time — correct boundaries, correct blank lines, a proper
// Content-Disposition header inside the part — so everything that read the MESSAGE was satisfied. Only a
// client that trusts BODYSTRUCTURE was wrong, and MailKit is not one: it fills missing extension data with
// NIL and carries on, which is why the existing IMAP tests never saw it.
//
// So these assertions are made against the RAW WIRE. A test written through MailKit would pass against the
// broken server, which is the whole lesson of the defect.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ImapBodyStructureTests
{
    private readonly E2EApiFactory _factory;

    public ImapBodyStructureTests(E2EApiFactory factory) => _factory = factory;

    private sealed record World(string Email, string ImapPassword, string RepoName, int Port);

    private async Task<World> SeedAsync(string fileName)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoName = $"ImapB{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"imap-bs-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-bs-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Structure User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        var davGen = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var basic = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davGen.GetProperty("password").GetString()}")));

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

        return new World(email, imapPassword, repoName,
            ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value);
    }

    // One FETCH, answered verbatim — no client library between the server and the assertion.
    private static async Task<string> FetchAsync(World world, string item)
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
        return await SendAsync("a3", $"FETCH 1 ({item})");
    }

    // THE defect. Without the disposition, a client has nothing in the structure telling it this part is a
    // file rather than something to show, and Apple Mail showed the base64.
    [Fact]
    public async Task Bodystructure_says_the_attachment_is_an_attachment()
    {
        var world = await SeedAsync("invoice.pdf");

        var response = await FetchAsync(world, "BODYSTRUCTURE");

        Assert.Contains("(\"ATTACHMENT\" (\"FILENAME\" \"invoice.pdf\"))", response, StringComparison.Ordinal);
    }

    // The other half, and the reason the parameter exists: BODY is the NON-extensible form and must stay
    // short. A fix that emitted the extension data unconditionally would look right in a client and be wrong
    // on the wire — the two responses are defined differently, and a client counts fields positionally.
    [Fact]
    public async Task Body_stays_the_non_extensible_form()
    {
        var world = await SeedAsync("invoice.pdf");

        var response = await FetchAsync(world, "BODY");

        Assert.DoesNotContain("ATTACHMENT", response, StringComparison.Ordinal);
    }

    // A synthetic attachment used to be application/octet-stream whatever it was — so a PDF arrived as an
    // anonymous blob a client could not preview, icon, or offer "open with" for. The bytes were always right;
    // the header saying what they are was missing.
    [Theory]
    [InlineData("invoice.pdf", "\"APPLICATION\" \"PDF\"")]
    [InlineData("scan.png", "\"IMAGE\" \"PNG\"")]
    public async Task The_attachments_media_type_comes_from_its_extension(string fileName, string expected)
    {
        var world = await SeedAsync(fileName);

        var response = await FetchAsync(world, "BODYSTRUCTURE");

        Assert.Contains(expected, response, StringComparison.Ordinal);
    }

    // The multipart's own extension data is ordered differently from a part's — parameters first — and the
    // boundary is what a client needs to make sense of the body it then fetches.
    [Fact]
    public async Task The_multiparts_extension_data_carries_its_boundary()
    {
        var world = await SeedAsync("invoice.pdf");

        var response = await FetchAsync(world, "BODYSTRUCTURE");

        Assert.Contains("\"MIXED\" (\"BOUNDARY\"", response, StringComparison.Ordinal);
    }
}
