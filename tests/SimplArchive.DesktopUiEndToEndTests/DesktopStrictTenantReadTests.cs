using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// THE DESKTOP CLIENT READING A STRICT TENANT THROUGH ITS OWN FUNNEL (#1421, the desktop half of #1408).
//
// #1408's server half is pinned by StrictTierEnvelopedReadTests: the door refuses a tokenless caller, serves
// with one, the envelope opens with the reader's key, an absent certificate means an absent rel. What none of
// that could prove is the half that actually broke — that THIS CLIENT asks correctly. The defect was entirely
// on the client side: a correct server and a correct-LOOKING client, with no test in between, because the
// funnel's credential decision was only ever exercised against a loopback stand-in that answers 200 to
// anything (DesktopContentAddressTests).
//
// So this drives `ApiCore.ReadContentAsync` — the ONE content funnel (ADR 0830), which is why preview, native
// open, drag-out and thumbnailing all get the envelope handling at once — against a real strict tenant on a
// real installation, and asserts the plaintext comes back.
//
// It is a PAIR of assertions about one decision, and only one of them was tested before: our own installation
// may need a bearer (the enveloped-content route is [Authorize]), and anywhere else must never receive one.
[Collection(UiCollection.Name)]
public class DesktopStrictTenantReadTests
{
    private const string Marker = "the strict tenant's plaintext";

    private readonly SelfHostedAppFixture _app;

    public DesktopStrictTenantReadTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_funnel_reads_a_strict_tenant_and_returns_the_plaintext_it_decrypted()
    {
        var reader = await StrictReaderAsync();

        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        ApiCore.Authenticated = Authed(reader.Token);

        // The reader's key, as the shipped client would find it in the user's certificate store. Injected
        // because a test host has no store to plant one in — and this hook is the same seam the card reader
        // plugs into (ADR 0830: the card is a second `Keys` source).
        var previous = EnvelopeOpener.Keys;
        EnvelopeOpener.Keys = () => new X509Certificate2Collection(reader.WithKey);
        try
        {
            var (bytes, _, wasEnveloped) = await ApiCore.ReadContentAsync(reader.DownloadHref);

            // ENVELOPED on the way in, PLAINTEXT on the way out. Both halves matter: the first says the
            // tenant really is strict and the funnel really met a CMS envelope, and the second that the
            // client opened it rather than handing the caller an unreadable blob.
            Assert.True(wasEnveloped, "a strict tenant must serve the content as an envelope");
            Assert.Equal(Marker, Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            EnvelopeOpener.Keys = previous;
            ApiCore.Authenticated = null;
        }
    }

    [Fact]
    public async Task The_funnel_sends_no_bearer_to_an_address_outside_this_installation()
    {
        // THE OTHER HALF OF THE SAME DECISION. A presigned object-storage URL carries its own signature, and
        // handing it our bearer would leak a credential to another service — some stores also refuse a
        // request carrying both, which reads as a signing bug. `ApiCore` decides by WHERE the address goes,
        // never by how the href was spelled, so this points it at a listener that is not this installation
        // and asserts nothing was sent.
        using var elsewhere = new AuthorizationRecordingListener();
        elsewhere.Start();

        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        ApiCore.Authenticated = Authed(await Ui.GetUserTokenAsync(_app.BaseUrl));
        try
        {
            var bytes = await ApiCore.GetContentBytesAsync($"{elsewhere.Url}/presigned-object");

            Assert.Equal("elsewhere", Encoding.UTF8.GetString(bytes));
            Assert.Null(elsewhere.LastAuthorization);
        }
        finally
        {
            ApiCore.Authenticated = null;
        }
    }

    private static HttpClient Authed(string token)
    {
        var http = new HttpClient { BaseAddress = new Uri(DesktopClientOptions.ApiBaseUrl!) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private sealed record StrictReader(string Token, string DownloadHref, X509Certificate2 WithKey);

    /// <summary>
    /// A provisioned strict tenant with a reader who holds a certificate, and one document to read.
    /// </summary>
    /// <remarks>
    /// The tenant is PROVISIONED over the API as the bootstrapped platform administrator, not inserted: the
    /// endpoint seeds its well-known masks and first repository, and a hand-built row would have neither —
    /// so the upload below would fail for a reason that has nothing to do with encryption.
    /// </remarks>
    private async Task<StrictReader> StrictReaderAsync()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var (clientId, clientSecret) = _app.PlatformAdmin;

        using var tokenResponse = await anonymous.PostAsync("connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }));
        tokenResponse.EnsureSuccessStatusCode();
        var platformToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;

        using var platform = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        platform.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);

        // The tenant's NAME is what carries the mode (ADR 0825), so it must be exactly the one the fixture
        // declared. Created once per run: the endpoint refuses a duplicate name, and both tests in this class
        // ask for it, so an existing tenant is a success rather than a problem.
        var email = $"strict-reader-{Guid.NewGuid():N}@desktop.e2e";
        using var created = await platform.PostAsJsonAsync("api/tenants", new
        {
            name = SelfHostedAppFixture.StrictTenantName,
            administratorEmail = email,
            administratorDisplayName = "Strict Reader",
            repositoryName = "Strict Repository",
        });

        var tenant = created.IsSuccessStatusCode
            ? await created.Content.ReadFromJsonAsync<JsonElement>()
            : throw new InvalidOperationException(
                $"could not provision {SelfHostedAppFixture.StrictTenantName}: {created.StatusCode} "
                + await created.Content.ReadAsStringAsync());

        var password = tenant.GetProperty("tenantAdministrator").GetProperty("password").GetString()!;
        var repositoryId = tenant.GetProperty("repository").GetProperty("id").GetGuid();

        var tenantId = tenant.GetProperty("id").GetGuid();
        var readerId = tenant.GetProperty("tenantAdministrator").GetProperty("id").GetGuid();

        var token = await Ui.GetUserTokenAsync(_app.BaseUrl, email, password);
        using var api = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // THE READER'S CERTIFICATE COMES FROM THE MODULE (ADR 0890). The encryption service's registry is retired,
        // self-service is closed on this tenant (ADR 0813), and a tenant whose mode delivers envelopes takes its
        // readers' certificates from an ACTIVATED module only — so this is how a real installation provisions a
        // reader: file the licence, activate, consent the module principal to its folder, enrol the certificate.
        var (pem, withKey) = NewReaderCertificate(email);

        var licenceId = await UploadSealedAsync(api, repositoryId, "test-module licence.json", ".json",
            Encoding.UTF8.GetBytes(_app.SignTestModuleLicence(tenantId)));
        (await api.PutAsJsonAsync("api/modules/test-module/license", new { licenseDocumentId = licenceId }))
            .EnsureSuccessStatusCode();

        var principalId = (await (await api.GetAsync("api/service-accounts")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("serviceAccounts").EnumerateArray()
            .Single(account => account.GetProperty("name").GetString() == "Module: Test Module")
            .GetProperty("id").GetGuid();
        var certificateFolder = (await (await api.PostAsJsonAsync("api/repositories",
                new { name = $"Reader certificates {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await api.PutAsJsonAsync($"api/documents/{certificateFolder}/acl-entries/service-accounts/{principalId}",
            new { canSee = true, canReadContent = true, canEditContent = true, canEditIndexData = true, canCreateSubItems = true }))
            .EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync($"api/test-module/reader-certificates?parentId={certificateFolder}",
            new { userId = readerId, certificatePem = pem })).EnsureSuccessStatusCode();

        var documentId = await UploadSealedAsync(api, repositoryId, "sealed.txt", ".txt", Encoding.UTF8.GetBytes(Marker));

        // The DOWNLOAD rel, followed rather than composed: on a strict tenant it is the enveloped-content
        // door, and that substitution is the thing under test.
        var versions = await (await api.GetAsync($"api/documents/{documentId}/versions"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var href = versions.GetProperty("versions").EnumerateArray().Last()
            .GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "download")
            .GetProperty("href").GetString()!;

        Assert.Contains("enveloped-content", href, StringComparison.Ordinal);

        return new StrictReader(token, href, withKey);
    }

    /// <summary>
    /// Files <paramref name="content"/> as a new document, ENCRYPTED CLIENT-SIDE — how an upload to a gated tenant
    /// actually works (ADR 0818).
    /// </summary>
    /// <remarks>
    /// The presign stays a presign and the CLIENT wraps the bytes, attaching the wrapped DEK at finalize. This used
    /// to be inline and claimed the encrypting decorator wrapped them on the way in — it does not, it never sees
    /// them, and the object landed as PLAINTEXT, which made the test exercise the mixed-state path while claiming to
    /// exercise encryption. Only an object carrying a wrapped DEK takes the route where the encryption service
    /// decrypts and envelopes (ADR 0862). A helper because the licence the module is activated with is filed the
    /// same way as the document under test.
    /// </remarks>
    private static async Task<Guid> UploadSealedAsync(
        HttpClient api, Guid parentId, string name, string extension, byte[] content)
    {
        var documentId = (await (await api.PostAsJsonAsync(
                $"api/documents/{parentId}/children", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var version = await (await api.PostAsJsonAsync(
                $"api/documents/{documentId}/versions", new { fileExtension = extension }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var encryption = version.GetProperty("encryption");

        var dek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var blob = new byte[12 + content.Length + 16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using (var aes = new System.Security.Cryptography.AesGcm(dek, 16))
        {
            aes.Encrypt(blob.AsSpan(0, 12), content, blob.AsSpan(12, content.Length), blob.AsSpan(^16..));
        }

        using var kek = System.Security.Cryptography.RSA.Create();
        kek.ImportFromPem(encryption.GetProperty("publicKeyPem").GetString()!);
        var wrappedDek = Convert.ToBase64String(
            kek.Encrypt(dek, System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256));

        // Straight to object storage, with no bearer — the same rule the funnel follows on the way out.
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(blob))).EnsureSuccessStatusCode();
        }

        var versionId = version.GetProperty("id").GetGuid();
        (await api.PutAsJsonAsync($"api/documents/{documentId}/versions/{versionId}",
            new { wrappedDek, kekGeneration = encryption.GetProperty("kekGeneration").GetString() }))
            .EnsureSuccessStatusCode();

        return documentId;
    }

    private static (string CertificatePem, X509Certificate2 WithKey) NewReaderCertificate(string email)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Exported and reloaded EXPORTABLE, which is not a detail. The software opener hands the certificate
        // to MimeKit's `context.Import(X509Certificate2)`, and that can only carry the private key across if
        // the platform will let it out — a key loaded without this flag imports as the PUBLIC half alone, so
        // decryption finds no private key and the opener declines with "no certificate on this computer holds
        // the key", which is indistinguishable from having the wrong certificate entirely. Measured: the
        // recipient and the held certificate matched on issuer AND serial, with HasPrivateKey true, and the
        // read still failed.
        var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        return (certificate.ExportCertificatePem(),
            X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.Exportable));
    }

    /// <summary>A listener that is NOT this installation, recording whether it was sent an Authorization header.</summary>
    private sealed class AuthorizationRecordingListener : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();

        public string Url { get; private set; } = string.Empty;

        public string? LastAuthorization { get; private set; }

        public void Start()
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    LastAuthorization = context.Request.Headers["Authorization"];
                    var bytes = Encoding.UTF8.GetBytes("elsewhere");
                    context.Response.ContentType = "application/octet-stream";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // The listener closed while waiting — the test is over.
                }
            });
        }

        public void Dispose()
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
        }
    }
}
