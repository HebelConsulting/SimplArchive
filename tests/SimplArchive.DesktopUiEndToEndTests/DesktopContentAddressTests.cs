using System.Net;
using System.Net.Http.Headers;
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
//
// IN THE UI COLLECTION, and not because it wants the fixture — it never touches it. `DesktopClientOptions.
// ApiBaseUrl` is process-global, and these tests are the only ones that both SET it and then make a real
// request that depends on it. Left uncollected, a test in the UI collection reassigning it to the self-hosted
// Api mid-flight sent this request there instead, and the loopback stand-in's assertion failed as a bare
// `404 (Not Found)` from a server it never meant to talk to — measured 1 failure in a full suite run, 5/5
// passing alone, which is exactly the shape that reads as a code regression. Joining the collection serialises
// it with every class that assigns that global to a live server.
//
// It costs nothing, which was worth measuring rather than assuming: no test here injects the fixture, so a
// filtered run of this class still finishes in 39 ms without standing the app up. The residual hazard is the
// "DesktopConfig" collection, whose logon tests also assign the global (to localhost:8080) and still run in
// parallel with this one — a collision there would surface as a connection failure rather than a 404, and the
// real fix is ONE collection for everything that mutates that global.
[Collection(UiCollection.Name)]
public class DesktopContentAddressTests : IDisposable
{
    private const string DoorPath = "/api/encrypted-content";

    // ApiCore.Authenticated is process-global and is populated by ANY test that signs in — a constructor sets
    // it. Restoring it here rather than trusting what ran before is the #1401 lesson applied in advance: this
    // class already had to be pulled into a collection because it both writes and reads process-global state,
    // and a second such static would have re-earned the same intermittent failure.
    private readonly HttpClient? _originalAuthenticated = ApiCore.Authenticated;

    public void Dispose() => ApiCore.Authenticated = _originalAuthenticated;

    [Fact]
    public async Task The_preview_funnel_fetches_a_RELATIVE_token_door_address()
    {
        using var installation = new Installation();
        var previous = DesktopClientOptions.ApiBaseUrl;
        DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;
        ApiCore.Authenticated = null;   // signed out, deterministically — not "whatever ran before this"
        try
        {
            var (bytes, contentType) = await SimplArchiveApiClient.DownloadAsync($"{DoorPath}?t=OPAQUE-TOKEN");

            Assert.Equal("PLAINTEXT-FROM-THE-DOOR", Encoding.ASCII.GetString(bytes));
            Assert.Equal("image/png", contentType);
            // The token has to survive the resolution: it IS the authorization, so a resolver that kept the
            // path and dropped the query would turn every preview into a 400 instead of an exception.
            Assert.Equal($"{DoorPath}?t=OPAQUE-TOKEN", installation.LastRequest);

            // Nothing is asserted here about credentials any more, and the reason is worth stating. This test
            // used to require that NO Authorization header was sent, on the grounds that "a header cannot
            // follow an address off-installation". That concern is real and unchanged — but it is about the
            // DESTINATION, and this address resolves to our own installation. It is now asserted where it
            // belongs, by A_presigned_address_ELSEWHERE_is_never_sent_the_bearer_token.
            //
            // The door itself is [AllowAnonymous] and authorizes by its `?t=` parameter, so a header it does
            // not read changes nothing. Keeping the old assertion would have meant deciding which relative
            // addresses may carry credentials by recognising their PATHS — exactly the composed-URL knowledge
            // ADR 0543 exists to keep out of clients.
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = previous;
        }
    }

    [Fact]
    public async Task An_authorized_content_route_on_OUR_installation_is_sent_the_bearer_token()
    {
        // WHAT BROKE, LIVE, on a strict tenant: "Could not load 'Invoice 2026-003': 401 (Unauthorized)".
        //
        // A third shape of content address arrived with the strict tier and it breaks the assumption the other
        // tests here encode. `/api/documents/…/enveloped-content` is RELATIVE, like the token door — but it is
        // an ordinary [Authorize] route that authorizes by HEADER, not by anything in the address. Sent through
        // the credential-free client it 401s, and every preview and download on such a tenant fails with it.
        //
        // So "does this address carry its own authorization?" is not answerable from the address, and the rule
        // is about the DESTINATION: credentials go to the installation we are signed in to, and nowhere else.
        using var installation = new Installation();
        // The route REFUSES without a bearer, exactly as the real one does. So this no longer inspects the
        // header afterwards — the fetch simply fails if the client does not send it, which is the difference
        // between a test that depends on the rule and one that remembers to check it.
        installation.BearerRequiredOn.Add("enveloped-content");

        var previousUrl = DesktopClientOptions.ApiBaseUrl;
        var previousClient = ApiCore.Authenticated;
        DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;
        using var signedIn = new HttpClient();
        signedIn.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "THE-SESSION-TOKEN");
        ApiCore.Authenticated = signedIn;
        try
        {
            var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(
                "/api/documents/d/versions/v/enveloped-content?inline=true");

            Assert.Equal("PLAINTEXT-FROM-THE-DOOR", Encoding.ASCII.GetString(bytes));
            Assert.Equal("/api/documents/d/versions/v/enveloped-content?inline=true", installation.LastRequest);
            Assert.Equal("Bearer THE-SESSION-TOKEN", installation.LastAuthorization);
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = previousUrl;
            ApiCore.Authenticated = previousClient;
        }
    }

    [Fact]
    public async Task THE_REGRESSION_a_signed_out_client_cannot_read_an_authorized_route()
    {
        // The defect exactly as it reached a live demonstration: "Could not load 'Invoice 2026-003': Response
        // status code does not indicate success: 401 (Unauthorized)".
        //
        // With no authenticated client the funnel falls back to the credential-free one — correct for a
        // presigned URL, and fatal for an [Authorize] route. This pins that the failure is a 401 from the
        // ROUTE rather than something the client swallows: before the fix it happened on every strict-tier
        // preview and download, and no test could see it because the stand-in answered 200 to everyone.
        using var installation = new Installation();
        installation.BearerRequiredOn.Add("enveloped-content");

        var previousUrl = DesktopClientOptions.ApiBaseUrl;
        var previousClient = ApiCore.Authenticated;
        DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;
        ApiCore.Authenticated = null;
        try
        {
            var thrown = await Assert.ThrowsAsync<HttpRequestException>(
                () => SimplArchiveApiClient.DownloadAsync("/api/documents/d/versions/v/enveloped-content"));

            Assert.Equal(HttpStatusCode.Unauthorized, thrown.StatusCode);
            Assert.Null(installation.LastAuthorization);
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = previousUrl;
            ApiCore.Authenticated = previousClient;
        }
    }

    [Fact]
    public async Task A_presigned_address_ELSEWHERE_is_never_sent_the_bearer_token()
    {
        // The other half, and the one that must not regress while fixing the first: object storage is a
        // different host in a split-network deployment (ObjectStorage:PublicServiceUrl), and a bearer token
        // following an address off-installation is the leak the credential-free client exists to prevent.
        // Signed in here, deliberately — before the fix there was no token to leak, so this asserts something
        // only now worth asserting.
        using var storage = new Installation();
        using var installation = new Installation();
        var previousUrl = DesktopClientOptions.ApiBaseUrl;
        var previousClient = ApiCore.Authenticated;
        DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;
        using var signedIn = new HttpClient();
        signedIn.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "THE-SESSION-TOKEN");
        ApiCore.Authenticated = signedIn;
        try
        {
            await SimplArchiveApiClient.DownloadAsync(
                $"{storage.BaseUrl}bucket/tenants/x/content.pdf?X-Amz-Signature=abc");

            Assert.Null(storage.LastAuthorization);
            Assert.Null(installation.LastRequest);
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = previousUrl;
            ApiCore.Authenticated = previousClient;
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
    /// <remarks>
    /// <b>It refuses what the real server refuses</b> (#1408). This stand-in used to answer <c>200</c> to
    /// anything, which is why it watched the strict tier's 401 go past: the test could only INSPECT the
    /// Authorization header afterwards, and a test that inspects rather than depends passes just as happily
    /// when the header is absent and nobody wrote the assertion. Making it reject like
    /// <c>DocumentVersionEnvelopedContentController</c> — <c>[Authorize]</c>, so no header means 401 — turns
    /// the credential rule into something the client must SATISFY rather than something a test remembers to
    /// look at.
    /// </remarks>
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

        /// <summary>
        /// Paths that require a bearer, mirroring an <c>[Authorize]</c> route.
        /// </summary>
        /// <remarks>
        /// A fragment rather than a whole path, so a test names the route it means
        /// (<c>enveloped-content</c>) without restating an address the server owns. The at-rest token door is
        /// deliberately NOT listed: it is <c>[AllowAnonymous]</c> and authorizes by its <c>?t=</c> parameter,
        /// so requiring a header there would be the stand-in inventing a contract the server does not have.
        /// </remarks>
        public List<string> BearerRequiredOn { get; } = [];

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

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                if (BearerRequiredOn.Any(fragment => path.Contains(fragment, StringComparison.Ordinal))
                    && string.IsNullOrEmpty(LastAuthorization))
                {
                    // Exactly what the real route does, and the whole point of this stand-in refusing at all:
                    // the client either sends the credential or it does not get the bytes.
                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    context.Response.Close();
                    continue;
                }

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
