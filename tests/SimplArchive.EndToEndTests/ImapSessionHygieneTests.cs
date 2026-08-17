using System.Net.Sockets;
using System.Text;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// Session hygiene (#562, ADR 0618): the pre-auth timeout, the per-user and total connection caps, and the
// RFC 3501 STATUS requested-items subset — driven over raw TCP because the point is exactly the protocol
// behavior a well-behaved client library would never trigger. The factory scales the knobs for testability
// (pre-auth 10 s, per-user 5, total 8); the authenticated idle timeout shares the same read-budget mechanism
// as pre-auth and deliberately keeps its 30-minute default (see E2EApiFactory).
[Collection(E2ECollection.Name)]
public class ImapSessionHygieneTests
{
    private readonly E2EApiFactory _factory;

    public ImapSessionHygieneTests(E2EApiFactory factory) => _factory = factory;

    private int Port => ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

    private static async Task<(TcpClient Client, StreamReader Reader, StreamWriter Writer)> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        return (client, reader, writer);
    }

    private static Task<string?> ReadLineAsync(StreamReader reader, int seconds = 30) =>
        reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(seconds));

    private async Task<string> SeedImapUserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"hygiene-{Guid.NewGuid():N}@e2e.local";
        const string password = "hygiene-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Hygiene User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        // Get-or-create the personal repository — INBOX only projects once it exists (the IMAP layer never provisions).
        await TestJson.Post(api, "/api/me/personal-repository", new { });
        var generated = await TestJson.Post(api, "/api/me/imap-access", new { });
        return $"{email} {generated.GetProperty("password").GetString()}";
    }

    [Fact]
    public async Task Unauthenticated_connection_is_dropped_after_the_preauth_timeout()
    {
        var (client, reader, _) = await ConnectAsync(Port);
        using var _1 = client;
        Assert.StartsWith("* OK", await ReadLineAsync(reader));

        // Send nothing: the 10 s pre-auth budget expires with a BYE, then the connection closes.
        Assert.Equal("* BYE login timed out", await ReadLineAsync(reader));
        Assert.Null(await ReadLineAsync(reader));
    }

    [Fact]
    public async Task Per_user_cap_refuses_the_excess_login_and_frees_on_disconnect()
    {
        var credentials = await SeedImapUserAsync();
        var port = Port;
        var held = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var (client, reader, writer) = await ConnectAsync(port);
                held.Add(client);
                Assert.StartsWith("* OK", await ReadLineAsync(reader));
                await writer.WriteLineAsync($"a1 LOGIN {credentials}");
                Assert.StartsWith("a1 OK", await ReadLineAsync(reader));
            }

            // The sixth authenticated session for the same user is refused — and stays unauthenticated.
            var (sixth, sixthReader, sixthWriter) = await ConnectAsync(port);
            held.Add(sixth);
            Assert.StartsWith("* OK", await ReadLineAsync(sixthReader));
            await sixthWriter.WriteLineAsync($"a1 LOGIN {credentials}");
            Assert.Equal("a1 NO too many connections for this user", await ReadLineAsync(sixthReader));

            // Releasing one slot lets the same session in on retry.
            held[0].Dispose();
            string? line = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(100);
                await sixthWriter.WriteLineAsync($"a2 LOGIN {credentials}");
                line = await ReadLineAsync(sixthReader);
                if (line != "a2 NO too many connections for this user")
                {
                    break;
                }
            }

            Assert.StartsWith("a2 OK", line!);
        }
        finally
        {
            foreach (var client in held)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Total_cap_says_bye_to_the_excess_connection()
    {
        var port = Port;
        var held = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var (client, reader, _) = await ConnectAsync(port);
                held.Add(client);
                Assert.StartsWith("* OK", await ReadLineAsync(reader));
            }

            var (ninth, ninthReader, _) = await ConnectAsync(port);
            held.Add(ninth);
            Assert.Equal("* BYE too many connections", await ReadLineAsync(ninthReader));
            Assert.Null(await ReadLineAsync(ninthReader));
        }
        finally
        {
            foreach (var client in held)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Status_returns_only_the_requested_items()
    {
        var credentials = await SeedImapUserAsync();
        var (client, reader, writer) = await ConnectAsync(Port);
        using var _1 = client;
        Assert.StartsWith("* OK", await ReadLineAsync(reader));
        await writer.WriteLineAsync($"a1 LOGIN {credentials}");
        Assert.StartsWith("a1 OK", await ReadLineAsync(reader));

        await writer.WriteLineAsync("a2 STATUS INBOX (MESSAGES)");
        var status = await ReadLineAsync(reader);
        Assert.NotNull(status);
        Assert.StartsWith("* STATUS", status);
        Assert.Contains("MESSAGES", status);
        foreach (var unrequested in new[] { "UIDNEXT", "UIDVALIDITY", "UNSEEN", "RECENT" })
        {
            Assert.DoesNotContain(unrequested, status);
        }

        Assert.StartsWith("a2 OK", await ReadLineAsync(reader));
        await writer.WriteLineAsync("a3 LOGOUT");
    }
}
