using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// A strict-tier tenant shares outward as an ENVELOPE or not at all (#1377, ADR 0827).
//
// An external link serves content anonymously, so there is no reader to envelope to and no certificate to ask
// for — which is why the tier refuses them by default (#1376). The exception is a link whose recipient is
// NAMED when it is created: the content then leaves as CMS addressed to that key, so the link stops being an
// anonymous plaintext door and becomes a delivery to a holder of a private key.
//
// The assertion that matters is the last one in the first test: the bytes the link serves are opened by the
// PRIVATE KEY belonging to the certificate that was supplied. Asserting the content type would only show that
// something was labelled as an envelope.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ExternalLinkEnvelopeTests
{
    private const string Marker = "EXTERNAL-LINK-CLEARTEXT-MARKER";

    private readonly E2EApiFactory _factory;

    public ExternalLinkEnvelopeTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_named_recipient_gets_an_envelope_their_own_key_opens()
    {
        var (api, documentId) = await SharedDocumentAsync(E2EApiFactory.StrictTenantName);
        var (pem, pkcs12) = NewRecipient("recipient@outside.example");

        var link = await TestJson.Post(api, $"/api/documents/{documentId}/external-links",
            new { recipientCertificatePem = pem });
        var token = TokenOf(link);

        // Anonymous, exactly as a recipient would: no account, no client, just the URL.
        using var stranger = _factory.CreateClient();
        using var response = await stranger.GetAsync($"/api/external-links/{token}/content?download=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pkcs7-mime", response.Content.Headers.ContentType?.MediaType);

        var served = await response.Content.ReadAsByteArrayAsync();

        // It is an envelope, not the document: the marker must NOT be readable in what crossed the wire.
        Assert.DoesNotContain(Marker, Encoding.ASCII.GetString(served), StringComparison.Ordinal);

        // And the recipient's own key opens it — the whole feature in one assertion.
        var message = await MimeMessage.LoadAsync(new MemoryStream(served));
        using var context = new TemporarySecureMimeContext();
        await context.ImportAsync(new MemoryStream(pkcs12), "recipient");
        var enveloped = Assert.IsAssignableFrom<ApplicationPkcs7Mime>(message.Body);
        var decrypted = Assert.IsAssignableFrom<MimePart>(enveloped.Decrypt(context));

        // DecodeTo, not WriteTo: the document rides as a base64 attachment, so the serialised entity would
        // never contain the marker in ASCII no matter how correct the envelope was. Decoding is also the only
        // form that proves the DOCUMENT came back rather than something merely shaped like it.
        using var opened = new MemoryStream();
        Assert.NotNull(decrypted.Content);
        await decrypted.Content!.DecodeToAsync(opened);
        Assert.Equal($"{Marker} inside the shared document\n", Encoding.ASCII.GetString(opened.ToArray()));

        // The filename survives the envelope, which is what lets the recipient save it as the document it is.
        // The document's own name plus the stored extension — so the recipient saves the document they were
        // sent rather than an opaque blob, and can tell what it is before opening it.
        Assert.EndsWith(".txt", decrypted.ContentDisposition?.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_strict_tenant_refuses_to_create_a_link_with_no_recipient()
    {
        var (api, documentId) = await SharedDocumentAsync(E2EApiFactory.StrictTenantName);

        using var response = await api.PostAsJsonAsync($"/api/documents/{documentId}/external-links", new { });

        // Refused at CREATION, which is the point: the content route already refuses to serve plaintext, so
        // without this the sharer would learn nothing and the RECIPIENT would discover the link never worked.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("EXTERNAL_LINK_CERTIFICATE_REQUIRED",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_unusable_certificate_is_refused_while_the_sharer_is_still_there()
    {
        var (api, documentId) = await SharedDocumentAsync(E2EApiFactory.StrictTenantName);

        using var response = await api.PostAsJsonAsync($"/api/documents/{documentId}/external-links",
            new { recipientCertificatePem = "-----BEGIN CERTIFICATE-----\nnot a certificate\n-----END CERTIFICATE-----" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_RECIPIENT_CERTIFICATE",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_ordinary_tenant_may_ask_for_an_envelope_too()
    {
        // The field is accepted everywhere, not only in the strict tier. A sharer who wants an envelope should
        // get one, and gating the FIELD on the tier would mean the same request succeeds or fails depending on
        // configuration the caller cannot see. The contrast also proves the enveloping is driven by the LINK
        // rather than by the tenant's mode.
        var (api, documentId) = await SharedDocumentAsync($"Ordinary{Guid.NewGuid():N}"[..24]);
        var (pem, _) = NewRecipient("someone@outside.example");

        var link = await TestJson.Post(api, $"/api/documents/{documentId}/external-links",
            new { recipientCertificatePem = pem });

        using var stranger = _factory.CreateClient();
        using var response = await stranger.GetAsync(
            $"/api/external-links/{TokenOf(link)}/content?download=true");

        Assert.Equal("application/pkcs7-mime", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_ordinary_link_still_redirects_to_the_bytes()
    {
        // The anti-vacuous half of the pair above: with no recipient named, nothing about this route changes,
        // so the tests cannot be passing because everything now returns an envelope.
        var (api, documentId) = await SharedDocumentAsync($"Ordinary{Guid.NewGuid():N}"[..24]);

        var link = await TestJson.Post(api, $"/api/documents/{documentId}/external-links", new { });

        // AllowAutoRedirect off, or the client follows the presigned URL to the real storage container and
        // reports the 200 it got there — which is a true statement about the wrong hop.
        using var stranger = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await stranger.GetAsync(
            $"/api/external-links/{TokenOf(link)}/content?download=true");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The token a created link redeems with — the last segment of the URL it hands back.</summary>
    private static string TokenOf(JsonElement created) =>
        new Uri(created.GetProperty("url").GetString()!).Segments[^1];

    /// <summary>A self-signed RSA recipient: the PEM the sharer supplies, and the p12 only they hold.</summary>
    private static (string Pem, byte[] Pkcs12) NewRecipient(string email)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.ExportCertificatePem(), certificate.Export(X509ContentType.Pkcs12, "recipient"));
    }

    /// <summary>A tenant, a signed-in sharer, and a document with content to share.</summary>
    private async Task<(HttpClient Api, Guid DocumentId)> SharedDocumentAsync(string tenantName)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName);

        // Off by default on a fresh tenant (ADR 0546) — the feature under test is what a link DELIVERS, not
        // whether the tenant permits links at all.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var tenant = await db.Tenants.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(t => t.Id == tenantId);
            tenant.AllowExternalLinks = true;
            await db.SaveChangesAsync();
        }

        var email = $"sharer-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "sh-1234", "Sharer",
            canManageRepositories: true, canCreateExternalLink: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "sh-1234"));

        var repository = (await TestJson.Post(api, "/api/repositories",
            new { name = $"repo-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var documentId = (await TestJson.Post(api, $"/api/documents/{repository}/children",
            new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        await UploadAsync(api, documentId, Encoding.ASCII.GetBytes($"{Marker} inside the shared document\n"));
        return (api, documentId);
    }

    private static async Task UploadAsync(HttpClient api, Guid documentId, byte[] plaintext)
    {
        var version = await TestJson.Post(api, $"/api/documents/{documentId}/versions",
            new { fileExtension = ".txt" });

        // A strict tenant is also an ENCRYPTED tenant, so the upload carries the client-side encryption
        // instruction (ADR 0818/B2) and the bytes must be wrapped before they are PUT. An ordinary tenant gets
        // no instruction and the same code path uploads the plaintext — which is what lets one helper serve
        // both, and what the envelope is then built over in either case.
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
