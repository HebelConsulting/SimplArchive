using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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

    private async Task<World> SeedAsync(string fileName, string? textContent = null)
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

        // Real text when a text document is under test — a text/plain part full of the PDF buffer's null bytes
        // would force MimeKit to base64-encode it, which is neither what a NOTAM is nor what the inline path
        // should produce.
        var bytes = textContent is { } t
            ? Encoding.UTF8.GetBytes(t)
            : new byte[2048];
        if (textContent is null)
        {
            Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(bytes, 0);
        }
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

    // A TEXT document's content is the message BODY, inline, not a multipart with the content behind the
    // signature as an attachment (#1153). The reason is a measured Apple Mail
    // behaviour: it fetches a message whole only up to ~18 KB, and above that fetches per-part, takes the
    // text part to render, and DEFERS the attachment — offering no affordance for it at all. A large NOTAM
    // briefing therefore rendered as our footer and nothing else, because the one part the reader wanted was
    // the one part Apple never fetched. Inline, the content is BODY[1], which is the part Apple always
    // fetches. So the structure of a text document is a SINGLE text part, not a MIXED multipart.
    [Fact]
    public async Task A_text_document_is_a_single_inline_text_part_not_an_attachment()
    {
        var world = await SeedAsync("briefing.txt", "B1000/26 NOTAMN E) Restricted area active.\n");

        var response = await FetchAsync(world, "BODYSTRUCTURE");

        // A single body-type-text, not a parenthesised list of parts: MIXED means the content was attached.
        Assert.Contains("BODYSTRUCTURE (\"TEXT\" \"PLAIN\"", response, StringComparison.Ordinal);
        Assert.DoesNotContain("MIXED", response, StringComparison.Ordinal);
        Assert.DoesNotContain("ATTACHMENT", response, StringComparison.Ordinal);
    }

    // And the inline body IS the document's own text, with the signature trailing it in the same part (#783) —
    // which is why this asserts the content is present rather than that the footer is absent.
    [Fact]
    public async Task The_text_body_carries_the_documents_own_content()
    {
        var world = await SeedAsync("briefing.txt", "B2000/26 NOTAMN E) Runway 09 closed for works.\n");

        var body = await FetchAsync(world, "BODY.PEEK[1]");

        Assert.Contains("B2000/26 NOTAMN E) Runway 09 closed", body, StringComparison.Ordinal);
        Assert.Contains("simplarchive.dev", body, StringComparison.Ordinal);
    }

    // The other direction must NOT regress: a BINARY document cannot be a text body, so it stays a multipart
    // with the content as an attachment. Pinning both arities keeps a later "just inline everything" change
    // from turning a PDF into mojibake in the message body.
    [Fact]
    public async Task A_binary_document_stays_a_multipart_attachment()
    {
        var world = await SeedAsync("invoice.pdf");

        var response = await FetchAsync(world, "BODYSTRUCTURE");

        Assert.Contains("\"MIXED\"", response, StringComparison.Ordinal);
        Assert.Contains("(\"ATTACHMENT\" (\"FILENAME\" \"invoice.pdf\"))", response, StringComparison.Ordinal);
    }

    // body-fld-lines is a COUNT, and we used to answer it with size/60 (#1141). For the synthetic detail body
    // that said 3 where the truth was 10.
    //
    // Note what shape this test has to be, because it is the point. Every assertion above is Contains, and a
    // substring can only ask whether something is PRESENT — it cannot notice that a number beside it is wrong,
    // so the defect sat underneath a file whose own header comment is about testing the raw wire.
    //
    // It also cannot use FetchAsync: that helper reads by LINES and rejoins them with '\n', which is lossless
    // enough for a structure line and destroys exactly what this test is about — the literal's byte count and
    // its CRLFs. So this one reads the socket as bytes, which is what "raw wire" has to mean here.
    [Fact]
    public async Task The_text_parts_line_count_is_the_bodys_real_line_count()
    {
        var world = await SeedAsync("invoice.pdf");

        var structure = await FetchAsync(world, "BODYSTRUCTURE");
        var body = await FetchRawAsync(world, "BODY.PEEK[1]");

        // ("TEXT" "PLAIN" (...) NIL NIL "7BIT" <octets> <lines> ...) — the field after the octet count.
        var text = Regex.Match(structure, "\"TEXT\" \"PLAIN\".*?\"7BIT\" (\\d+) (\\d+)");
        Assert.True(text.Success, $"No text part in the structure: {structure}");
        var declaredOctets = int.Parse(text.Groups[1].Value, CultureInfo.InvariantCulture);
        var declaredLines = int.Parse(text.Groups[2].Value, CultureInfo.InvariantCulture);

        // The literal the server announced, then exactly that many octets of it — the bytes it CLAIMS the
        // part is, rather than anything this test reconstructs.
        var literal = Regex.Match(body, @"BODY\[1\] \{(\d+)\}\r\n");
        Assert.True(literal.Success, $"No BODY[1] literal in the response: {body}");
        var octets = int.Parse(literal.Groups[1].Value, CultureInfo.InvariantCulture);
        var served = body.Substring(literal.Index + literal.Length, octets);

        Assert.Equal(octets, declaredOctets);

        // A final line without a terminator still counts, which is why this is not simply a count of '\n'.
        var actualLines = served.Count(c => c == '\n') + (served.Length > 0 && !served.EndsWith('\n') ? 1 : 0);
        Assert.Equal(actualLines, declaredLines);
    }

    // The same conversation as FetchAsync, read as BYTES: Latin-1 so one byte is one char and a literal's
    // octet count indexes the string directly.
    private static async Task<string> FetchRawAsync(World world, string item)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", world.Port);
        using var stream = tcp.GetStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        var buffer = new byte[64 * 1024];

        // A BUDGET on every read, so a protocol mistake in this helper fails the test instead of hanging it.
        // Learned at the desk: a wait for a line that could never arrive sat at 0.2% CPU for 29 minutes and
        // looked exactly like a slow container start. A test that cannot finish is worse than one that fails,
        // because only the second one tells you which.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Done when the TAGGED line has arrived. It must be matched at a line START — either the very first
        // thing in this read or after a CRLF — and not merely "after a CRLF": the reply to the first command
        // begins at position 0 of a fresh buffer, so waiting for a preceding CRLF waits forever. (It did:
        // 29 minutes at 0.2% CPU, which is what a hang looks like when you are expecting slowness.)
        static bool Complete(StringBuilder sb, string tag)
        {
            var text = sb.ToString();
            return text.StartsWith($"{tag} ", StringComparison.Ordinal)
                || text.Contains($"\r\n{tag} ", StringComparison.Ordinal);
        }

        async Task<string> ReadUntilAsync(string tag)
        {
            var sb = new StringBuilder();
            while (!Complete(sb, tag))
            {
                var read = await stream.ReadAsync(buffer, budget.Token);
                if (read == 0)
                {
                    break;
                }

                sb.Append(Encoding.Latin1.GetString(buffer, 0, read));
            }

            return sb.ToString();
        }

        // The greeting, with ONE read: it is unsolicited, so there is no tag to wait for — and waiting for a
        // tagged line here deadlocks against a command that has not been sent yet.
        var greeting = await stream.ReadAsync(buffer, budget.Token);
        Assert.True(greeting > 0, "The server closed the connection before greeting.");

        await SendAndReadAsync("a1", $"LOGIN \"{world.Email}\" \"{world.ImapPassword}\"");
        await SendAndReadAsync("a2", $"SELECT \"{world.RepoName}\"");
        return await SendAndReadAsync("a3", $"FETCH 1 ({item})");

        async Task<string> SendAndReadAsync(string tag, string command)
        {
            await writer.WriteLineAsync($"{tag} {command}");
            return await ReadUntilAsync(tag);
        }
    }
}
