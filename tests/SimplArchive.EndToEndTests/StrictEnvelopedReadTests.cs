using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The strict tier's DELIVERY step (#1393, ADR 0828): a signed-in reader's content leaves as a CMS envelope
// addressed to their registered certificate, or it does not leave.
//
// #1376 built the other half — the refusal — and this is what makes the tier a tier rather than a wall. The
// decisive assertion is that the reader's OWN private key opens what the API served; asserting the content
// type would only show that something was labelled an envelope.
//
// The rel is the other half of the story and is tested with it, because the two must agree: a strict-tier
// reader WITH a certificate is offered `download`, one WITHOUT is offered nothing and told to enrol. A rel
// present on one answer and a request served on another is the lying affordance ADR 0543 forbids.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StrictEnvelopedReadTests
{
    private const string Marker = "STRICT-READ-CLEARTEXT-MARKER";

    private readonly E2EApiFactory _factory;

    public StrictEnvelopedReadTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_reader_with_a_certificate_gets_an_envelope_their_own_key_opens()
    {
        var reader = await StrictReaderAsync(withCertificate: true);
        var (api, documentId, pkcs12) = (reader.Api, reader.DocumentId, reader.Pkcs12);

        var version = await CurrentVersionAsync(api, documentId);
        var download = Rel(version, "download");
        Assert.NotNull(download);

        using var response = await api.GetAsync(download);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pkcs7-mime", response.Content.Headers.ContentType?.MediaType);

        var served = await response.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain(Marker, Encoding.ASCII.GetString(served), StringComparison.Ordinal);

        var message = await MimeMessage.LoadAsync(new MemoryStream(served));
        using var context = new TemporarySecureMimeContext();
        await context.ImportAsync(new MemoryStream(pkcs12!), "reader");
        var enveloped = Assert.IsAssignableFrom<ApplicationPkcs7Mime>(message.Body);
        var decrypted = Assert.IsAssignableFrom<MimePart>(enveloped.Decrypt(context));

        using var opened = new MemoryStream();
        Assert.NotNull(decrypted.Content);
        await decrypted.Content!.DecodeToAsync(opened);
        Assert.Contains(Marker, Encoding.ASCII.GetString(opened.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reader_without_a_certificate_is_offered_nothing_rather_than_a_button_that_fails()
    {
        var reader = await StrictReaderAsync(withCertificate: false);
        var (api, documentId) = (reader.Api, reader.DocumentId);

        var version = await CurrentVersionAsync(api, documentId);

        // ADR 0543: absence means "not available to you, here, now". The client offers enrolment instead of a
        // download that would refuse — which is the whole reason the rel is gated on the READER rather than on
        // the tenant.
        Assert.Null(Rel(version, "download"));
        Assert.Null(Rel(version, "preview"));

        // And the metadata still renders. A refusal that took the resource with it would leave a strict tenant
        // able to see nothing at all, which is why the seam answers null rather than throwing (#1387).
        Assert.False(string.IsNullOrEmpty(version.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task The_route_refuses_a_reader_who_lost_their_certificate_between_reading_and_fetching()
    {
        // The honest reachable case for a refusal at the door: the rel was emitted, then the certificate was
        // removed. Never plaintext — that would turn an unreachable branch into a silent hole in the tier's
        // only guarantee.
        var reader = await StrictReaderAsync(withCertificate: true);
        var (api, documentId, tenantId, email) = (reader.Api, reader.DocumentId, reader.TenantId, reader.Email);
        var download = Rel(await CurrentVersionAsync(api, documentId), "download")!;

        await RegisterCertificateAsync(tenantId, email, null);

        using var response = await api.GetAsync(download);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CONTENT_CANNOT_BE_ENVELOPED",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_ordinary_tenant_still_gets_a_presigned_url()
    {
        // The contrast that stops all of this passing on a build which envelopes everything — and the check
        // that the strict path is driven by the TENANT's mode rather than by anybody having a certificate.
        var ordinary = await ReaderAsync($"Ordinary{Guid.NewGuid():N}"[..24], withCertificate: true);
        var (api, documentId) = (ordinary.Api, ordinary.DocumentId);

        var download = Rel(await CurrentVersionAsync(api, documentId), "download");

        Assert.NotNull(download);
        Assert.StartsWith("http", download, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_find_in_document_overlay_is_refused_because_its_words_are_the_document()
    {
        // A text layout is every word with its coordinates: a complete plaintext reconstruction, through a rel
        // advertised on every version and a route that never presigns anything — which is exactly why #1376's
        // refusal did not reach it (#1394).
        var reader = await StrictReaderAsync(withCertificate: true);
        var (api, documentId) = (reader.Api, reader.DocumentId);
        var versionId = (await CurrentVersionAsync(api, documentId)).GetProperty("id").GetString();

        using var response = await api.GetAsync($"/api/documents/{documentId}/versions/{versionId}/text-layout");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PLAINTEXT_CONTENT_REFUSED",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_mounted_drive_is_refused_rather_than_served_the_file()
    {
        // The sharpest of the doors #1394 found: #1376 RECORDED WebDAV as a blind door that is refused, and it
        // was never refused — it simply was not reachable through a presign, so the decision was written and
        // the enforcement was not. A mounted drive hands bytes to an operating system that cannot decrypt CMS.
        var reader = await StrictReaderAsync(withCertificate: true);
        var davPassword = (await TestJson.Post(reader.Api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;

        // The REAL path — repository, then document. An earlier version of this test asked for the document at
        // the mount root, got a 404 for the wrong reason, and passed while proving nothing.
        var path = $"/SimplArchive/{reader.RepositoryName}/{reader.DocumentName}.txt";

        using var dav = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{reader.Email}:{davPassword}"))) },
        };

        using var response = await dav.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // 409 and the code, not merely "not 200" — a 404 would satisfy a weaker assertion while proving the
        // path was wrong rather than the door closed, which is exactly how the first version of this test
        // passed without exercising anything.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("PLAINTEXT_CONTENT_REFUSED", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, body, StringComparison.Ordinal);
    }

    private static string? Rel(JsonElement version, string rel) =>
        version.TryGetProperty("links", out var links)
            ? links.EnumerateArray()
                .Where(l => l.GetProperty("rel").GetString() == rel)
                .Select(l => l.GetProperty("href").GetString())
                .FirstOrDefault()
            : null;

    private static async Task<JsonElement> CurrentVersionAsync(HttpClient api, Guid documentId)
    {
        var versions = await TestJson.Get(api, $"/api/documents/{documentId}/versions");
        return versions.GetProperty("versions").EnumerateArray().Last();
    }

    private Task<Reader> StrictReaderAsync(bool withCertificate) =>
        ReaderAsync(E2EApiFactory.StrictTenantName, withCertificate);

    private sealed record Reader(HttpClient Api, Guid DocumentId, byte[]? Pkcs12, Guid TenantId, string Email, string RepositoryName, string DocumentName);

    /// <summary>
    /// Writes the reader's certificate the way it actually arrives in this tier — from OUTSIDE.
    /// </summary>
    /// <remarks>
    /// Not through <c>PUT /api/me/smime-certificate</c>, which REFUSES here by design: self-service is closed
    /// for any tenant the envelope client is enabled for, because those identities are provisioned by the
    /// encryption service rather than pasted by their owner (ADR 0813). A strict tenant is encryption-enabled
    /// by definition, so the self-service endpoint can never be its registration path — writing the column
    /// directly is what that service does, and the test would otherwise be exercising a door the tier closes.
    /// </remarks>
    private async Task RegisterCertificateAsync(Guid tenantId, string email, string? pem)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var user = await db.Users.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(u => u.TenantId == tenantId && u.NormalizedEmail == email.ToUpperInvariant());
        user.SmimeCertificatePem = pem;
        await db.SaveChangesAsync();
    }

    /// <summary>A tenant, a reader (optionally with a registered certificate), and a document to read.</summary>
    private async Task<Reader> ReaderAsync(string tenantName, bool withCertificate)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName);
        var email = $"reader-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "rd-1234", "Reader", canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "rd-1234"));

        byte[]? pkcs12 = null;
        if (withCertificate)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            pkcs12 = certificate.Export(X509ContentType.Pkcs12, "reader");
            await RegisterCertificateAsync(tenantId, email, certificate.ExportCertificatePem());
        }

        var repositoryName = $"repo-{Guid.NewGuid():N}";
        var repository = (await TestJson.Post(api, "/api/repositories",
            new { name = repositoryName })).GetProperty("id").GetGuid();
        var documentName = $"doc-{Guid.NewGuid():N}";
        var documentId = (await TestJson.Post(api, $"/api/documents/{repository}/children",
            new { name = documentName })).GetProperty("id").GetGuid();

        await UploadAsync(api, documentId, Encoding.ASCII.GetBytes($"{Marker} the document a card opens\n"));
        return new Reader(api, documentId, pkcs12, tenantId, email, repositoryName, documentName);
    }

    /// <summary>Uploads through the real contract — client-side encrypted where the tenant demands it.</summary>
    private static async Task UploadAsync(HttpClient api, Guid documentId, byte[] plaintext)
    {
        var version = await TestJson.Post(api, $"/api/documents/{documentId}/versions", new { fileExtension = ".txt" });

        var body = plaintext;
        object finalize = new { };
        if (version.TryGetProperty("encryption", out var encryption) && encryption.ValueKind == JsonValueKind.Object)
        {
            var dek = RandomNumberGenerator.GetBytes(32);
            var blob = new byte[12 + plaintext.Length + 16];
            RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            using (var aes = new AesGcm(dek, 16))
            {
                aes.Encrypt(blob.AsSpan(0, 12), plaintext, blob.AsSpan(12, plaintext.Length), blob.AsSpan(^16..));
            }

            using var kek = RSA.Create();
            kek.ImportFromPem(encryption.GetProperty("publicKeyPem").GetString()!);
            body = blob;
            finalize = new
            {
                wrappedDek = Convert.ToBase64String(kek.Encrypt(dek, RSAEncryptionPadding.OaepSHA256)),
                kekGeneration = encryption.GetProperty("kekGeneration").GetString()!,
            };
        }

        using var anonymous = new HttpClient();
        var put = await anonymous.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(body));
        Assert.True(put.IsSuccessStatusCode, $"presigned PUT answered {(int)put.StatusCode}");

        var self = version.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "self").GetProperty("href").GetString()!;
        await TestJson.Put(api, self, finalize);
    }
}
