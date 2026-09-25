using System.Net;
using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// A content address the server hands the desktop may be ABSOLUTE or RELATIVE, and the client must fetch
// either (ADR 0818).
//
// WHAT BROKE, ON A LIVE INSTALLATION. A presigned object-storage URL is absolute; the at-rest encryption
// token door is "/api/encrypted-content?t=…" and deliberately relative, because the server hands ONE href
// to both clients and a browser resolves it against the page origin. The desktop's content clients have no
// BaseAddress by design — that is what stops a bearer token ever being sent to a presigned S3 URL — so a
// relative href produced "An invalid request URI was provided. Either the request URI must be an absolute
// URI or BaseAddress must be set." Every document in an encrypted tenant failed to preview, and the web
// client beside it was fine, which is what kept it invisible.
//
// Nothing had regressed: the token door simply arrived after the code that assumed every content address
// was absolute, and that assumption was written down as a COMMENT ("a presigned URL — no auth") rather than
// expressed anywhere the compiler or a test could see it.
//
// So these drive the real funnels against a real listener rather than asserting on the resolver's return
// value. A test of the resolver alone would pass on a client that never called it — which is precisely the
// defect, three times over: the preview funnel, the open-in-native-application downloader and the
// page-thumbnail loader each held their own base-less HttpClient.
public class DesktopContentAddressTests
{
    private const string DoorPath = "/api/encrypted-content";

    [Fact]
    public async Task The_preview_funnel_fetches_a_RELATIVE_token_door_address()
    {
        using var installation = new Installation();
        var previous = DesktopClientOptions.ApiBaseUrl;
        DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;
        try
        {
            var (bytes, contentType) = await SimplArchiveApiClient.DownloadAsync($"{DoorPath}?t=OPAQUE-TOKEN");

            Assert.Equal("PLAINTEXT-FROM-THE-DOOR", Encoding.ASCII.GetString(bytes));
            Assert.Equal("image/png", contentType);
            // The token has to survive the resolution: it IS the authorization, so a resolver that kept the
            // path and dropped the query would turn every preview into a 400 instead of an exception.
            Assert.Equal($"{DoorPath}?t=OPAQUE-TOKEN", installation.LastRequest);
            // And nothing may add credentials on the way — the door authenticates by its token, and this
            // client is base-less exactly so that a header cannot follow an address off-installation.
            Assert.Null(installation.LastAuthorization);
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = previous;
        }
    }

    [Fact]
    public async Task Open_in_the_native_application_fetches_a_RELATIVE_token_door_address()
    {
        // The second door, and the one this client exists for (ADR 0236): it downloads to a temp file with
        // its own client, so the preview being fixed says nothing about it.
        using var installation = new Installation();
        var previousUrl = DesktopClientOptions.ApiBaseUrl;
        var previousTemp = NativeFileOpener.TempDirectoryOverride;
        var temp = Directory.CreateTempSubdirectory("sa-content-address").FullName;
        DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;
        NativeFileOpener.TempDirectoryOverride = temp;
        try
        {
            var path = await NativeFileOpener.DownloadToTempAsync($"{DoorPath}?t=OPAQUE-TOKEN", "invoice.pdf");

            Assert.Equal("PLAINTEXT-FROM-THE-DOOR", await File.ReadAllTextAsync(path));
            Assert.Equal($"{DoorPath}?t=OPAQUE-TOKEN", installation.LastRequest);
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = previousUrl;
            NativeFileOpener.TempDirectoryOverride = previousTemp;
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task An_ABSOLUTE_presigned_address_is_left_alone()
    {
        // The other half, and the reason this cannot simply be "always prepend the installation": a presigned
        // URL points at object storage, which in a split-network deployment is not the Api's host at all
        // (ObjectStorage:PublicServiceUrl). Rewriting it would break every unencrypted tenant.
        using var storage = new Installation();
        using var installation = new Installation();
        var previous = DesktopClientOptions.ApiBaseUrl;
        DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;
        try
        {
            var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(
                $"{storage.BaseUrl}bucket/tenants/x/content.pdf?X-Amz-Signature=abc");

            Assert.Equal("PLAINTEXT-FROM-THE-DOOR", Encoding.ASCII.GetString(bytes));
            Assert.Equal("/bucket/tenants/x/content.pdf?X-Amz-Signature=abc", storage.LastRequest);
            Assert.Null(installation.LastRequest);
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = previous;
        }
    }

    // Every base-less HttpClient in this client is a door a relative address can break, and one of the three
    // broke SILENTLY — the thumbnail loader's catch answers "no thumbnails", so an encrypted tenant would
    // have shown an empty sort-pages dialog with nothing to read anywhere. Content reads are therefore
    // consolidated onto ApiCore, and this pins the list so a new private client has to justify itself.
    [Fact]
    public void No_new_private_HttpClient_appears_without_a_reason()
    {
        if (RepoRoot() is not { } root)
        {
            return;
        }

        // Each of these builds its address from configuration and is absolute by construction, so none of
        // them is a content reader. ApiCore holds the two that are (the anonymous content client and the
        // authenticated one); a file appearing here that FETCHES A SERVER-SUPPLIED HREF is the bug above.
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Services/ApiCore.cs"] = "the authenticated client and the anonymous content client — the one place a content address is resolved",
            ["Services/SimplArchiveApiClient.cs"] = "the impersonation token exchange; carries a BaseAddress and posts to connect/token",
            ["Services/ClientUpdate.cs"] = "the version manifest and the releases API — absolute, from configuration",
            ["Services/ServerReachability.cs"] = "the discovery document at a configured base URL",
            ["Services/ServerIdentity.cs"] = "the API root at a configured base URL",
        };

        var found = Directory
            .EnumerateFiles(Path.Combine(root, "src", "SimplArchive.DesktopClient"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("new HttpClient", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Path.Combine(root, "src", "SimplArchive.DesktopClient"), f)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // Anti-vacuous: the scan must still see the clients that ARE there, or an allowlist check passes by
        // finding nothing at all.
        Assert.Contains("Services/ApiCore.cs", found);

        var unexpected = found.Where(f => !allowed.ContainsKey(f)).ToList();
        Assert.True(unexpected.Count == 0,
            "These files construct their own HttpClient:\n  " + string.Join("\n  ", unexpected)
            + "\n\nIf it fetches an address the SERVER handed you — a preview, a download, a page image — use "
            + "ApiCore.GetContentAsync instead: a content address may be RELATIVE (the at-rest encryption "
            + "token door, ADR 0818) and a base-less client throws 'An invalid request URI was provided' on "
            + "one. That broke every preview and every native open on an encrypted tenant. If it is genuinely "
            + "not a content reader, add it here with the reason.");

        var gone = allowed.Keys.Where(f => !found.Contains(f)).ToList();
        Assert.True(gone.Count == 0,
            "These no longer construct an HttpClient, so their entries are stale: " + string.Join(", ", gone));
    }

    private static string? RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimplArchive.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    /// <summary>A loopback stand-in for one host — the installation, or an object store.</summary>
    private sealed class Installation : IDisposable
    {
        private readonly HttpListener _listener = new();

        public Installation()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public string? LastRequest { get; private set; }

        public string? LastAuthorization { get; private set; }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // disposed
                }

                LastRequest = context.Request.Url?.PathAndQuery;
                LastAuthorization = context.Request.Headers["Authorization"];
                var body = Encoding.ASCII.GetBytes("PLAINTEXT-FROM-THE-DOOR");
                context.Response.ContentType = "image/png";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }
}
