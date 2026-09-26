using System.Net;
using System.Net.Http.Headers;
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

// The strict tier's READ path over the real API (#1408, ADR 0830) — the coverage whose absence let three
// defects reach a live demonstration with every suite green.
//
// The worst of them was the simplest: `enveloped-content` is advertised as a RELATIVE href, exactly like the
// at-rest token door, but unlike that door it is an ordinary [Authorize] route that authorizes by HEADER. The
// desktop's content funnel is deliberately credential-free — a bearer must never follow an address that might
// be a presigned object-storage URL — so it sent no token, the server correctly answered 401, and every strict
// read failed. Nothing could see it: DesktopContentAddressTests drives the funnel against a loopback stand-in
// that answers 200 to anything, the card check builds its envelope locally and never fetches over HTTP, and
// the manual verification used curl WITH a bearer, which is precisely what the client did not send.
//
// So the first test below is the one that matters: the route refuses an anonymous caller. That is not a
// complaint about the route — it is the correct behaviour, pinned, so that the reason the funnel must send a
// token to its OWN installation is written down somewhere a test enforces.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StrictTierEnvelopedReadTests
{
    private const string Marker = "STRICT-TIER-PLAINTEXT-MARKER";

    private readonly E2EApiFactory _factory;

    public StrictTierEnvelopedReadTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_envelope_door_refuses_a_caller_carrying_no_token()
    {
        var reader = await StrictReaderAsync(withCertificate: true);

        // Anonymous, which is what the desktop's credential-free funnel amounted to (#1408 defect 1). The
        // address is relative and looks exactly like the at-rest token door — which needs no credential — so
        // this is the distinction that has to hold, and it is invisible from the href alone.
        using var withoutToken = _factory.CreateClient();
        using var refused = await withoutToken.GetAsync(reader.EnvelopeHref);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // And the same address WITH the bearer serves the envelope — so the test cannot be passing because the
        // route is broken or gone, which a 401-only assertion would not distinguish.
        using var served = await reader.Api.GetAsync(reader.EnvelopeHref);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("application/pkcs7-mime", served.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_registered_certificate_turns_the_door_into_an_envelope_its_own_key_opens()
    {
        var reader = await StrictReaderAsync(withCertificate: true);

        using var response = await reader.Api.GetAsync(reader.EnvelopeHref);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // Not the document: the marker must not be readable in what crossed the wire.
        Assert.DoesNotContain(Marker, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);

        // And the reader's own private key opens it — the whole read path in one assertion. Asserting the
        // content type would only show that something was LABELLED as an envelope.
        var message = await MimeMessage.LoadAsync(new MemoryStream(bytes));
        using var context = new TemporarySecureMimeContext();
        await context.ImportAsync(new MemoryStream(reader.Pkcs12), "reader");
        var enveloped = Assert.IsAssignableFrom<ApplicationPkcs7Mime>(message.Body);
        var decrypted = Assert.IsAssignableFrom<MimePart>(enveloped.Decrypt(context));

        using var opened = new MemoryStream();
        Assert.NotNull(decrypted.Content);
        await decrypted.Content!.DecodeToAsync(opened);
        Assert.Equal($"{Marker} inside the strict document\n", Encoding.ASCII.GetString(opened.ToArray()));
    }

    [Fact]
    public async Task With_no_certificate_the_rel_is_absent_and_forcing_the_route_is_refused_rather_than_served()
    {
        var reader = await StrictReaderAsync(withCertificate: false);

        // Absent, not empty (ADR 0543): there is no door for this reader, and the client disables the
        // affordance rather than offering one that fails.
        Assert.Null(reader.EnvelopeHrefOrNull);

        // A client that reaches the route anyway — a stale address, a hand-built URL — is REFUSED. This is the
        // assertion that would catch the worst possible regression in this area, a fall-through to plaintext:
        // 409 says "this tenant envelopes and you have no usable certificate", and the body is not the document.
        using var forced = await reader.Api.GetAsync(reader.ForcedEnvelopeHref);

        Assert.Equal(HttpStatusCode.Conflict, forced.StatusCode);
        Assert.DoesNotContain(Marker, await forced.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_tenant_still_gets_a_presigned_download_and_no_envelope_door()
    {
        // The contrast, without which every assertion above could be passing on an installation where content
        // reads are simply broken. An ordinary tenant's version advertises `download` as an ABSOLUTE storage
        // URL — the presigned object — and offers no envelope door at all.
        var reader = await ReaderAsync($"Ordinary{Guid.NewGuid():N}"[..24], withCertificate: true);

        Assert.Null(reader.EnvelopeHrefOrNull);

        var download = Href(reader.Version, "download");
        Assert.NotNull(download);
        Assert.StartsWith("http", download, StringComparison.Ordinal);
    }

    // THE TENANT SHAPE ADR 0813 ACTUALLY DESCRIBES, and the one that did not work (#1433). Self-service is
    // closed for exactly the tenants the envelope client serves, so nothing writes User.SmimeCertificatePem —
    // and this read path consulted only that column, so every reader on a properly-configured strict tenant got
    // a permanent 409. The certificate lives in the Encryption Service's registry, which has a real provisioning
    // door and which the notification dispatcher has always consulted; this path was the one that did not.
    [Fact]
    public async Task A_certificate_held_by_the_SERVICE_is_found_and_the_envelope_opens_with_its_key()
    {
        var reader = await StrictReaderAsync(withCertificate: false, withRegistryCertificate: true);

        // The rel is advertised at all — which is the half that was broken: with nothing on the user row and the
        // registry unconsulted, the resource carried no door and the client correctly offered nothing.
        using var response = await reader.Api.GetAsync(reader.EnvelopeHref);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain(Marker, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);

        // And it is addressed to the REGISTRY's key. Asserting the 200 alone would pass on an envelope built to
        // some other certificate, which is exactly the mistake a "found it" check cannot see.
        var message = await MimeMessage.LoadAsync(new MemoryStream(bytes));
        using var context = new TemporarySecureMimeContext();
        await context.ImportAsync(new MemoryStream(reader.Pkcs12), "reader");
        var enveloped = Assert.IsAssignableFrom<ApplicationPkcs7Mime>(message.Body);
        var decrypted = Assert.IsAssignableFrom<MimePart>(enveloped.Decrypt(context));

        using var opened = new MemoryStream();
        Assert.NotNull(decrypted.Content);
        await decrypted.Content!.DecodeToAsync(opened);
        Assert.Equal($"{Marker} inside the strict document\n", Encoding.ASCII.GetString(opened.ToArray()));
    }

    [Fact]
    public async Task The_users_OWN_certificate_still_wins_over_the_registry()
    {
        // The precedence, which matches the notification dispatcher's (`column ?? registry`). It matters because
        // the desktop's card flow registers a certificate the USER holds (ADR 0831) — a registry entry must not
        // override the key actually in the reader's hand, or the envelope would be addressed to something they
        // cannot open.
        var reader = await StrictReaderAsync(withCertificate: true, withRegistryCertificate: true);

        using var response = await reader.Api.GetAsync(reader.EnvelopeHref);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // Opened with the USER's key, not the registry's — the registry's p12 is deliberately not imported here.
        var message = await MimeMessage.LoadAsync(new MemoryStream(bytes));
        using var context = new TemporarySecureMimeContext();
        await context.ImportAsync(new MemoryStream(reader.Pkcs12), "reader");
        var enveloped = Assert.IsAssignableFrom<ApplicationPkcs7Mime>(message.Body);
        Assert.IsAssignableFrom<MimePart>(enveloped.Decrypt(context));
    }

    private sealed record Reader(
        HttpClient Api, JsonElement Version, string? EnvelopeHrefOrNull, string ForcedEnvelopeHref, byte[] Pkcs12)
    {
        public string EnvelopeHref => EnvelopeHrefOrNull
            ?? throw new InvalidOperationException("the version advertises no enveloped-content address");
    }

    private Task<Reader> StrictReaderAsync(bool withCertificate, bool withRegistryCertificate = false) =>
        ReaderAsync(E2EApiFactory.StrictTenantName, withCertificate, withRegistryCertificate);

    /// <summary>A signed-in reader in a tenant, a document with content, and that version's addresses.</summary>
    private async Task<Reader> ReaderAsync(
        string tenantName, bool withCertificate, bool withRegistryCertificate = false)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName);

        var email = $"strict-reader-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "sr-1234", "Strict Reader", canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "sr-1234"));

        var (pem, pkcs12) = NewReaderCertificate(email);
        if (withCertificate)
        {
            // PLANTED on the user row rather than registered over `PUT /api/me/smime-certificate`, and the
            // reason is a real property of the tier rather than a shortcut: self-service is CLOSED for exactly
            // the tenants the envelope client serves (ADR 0813), so that endpoint answers 409 here. A strict
            // tenant's identities are provisioned from outside, and this is that provisioning. Measured, not
            // assumed — the first version of this test registered over the API and was refused.
            //
            // (Whether a tenant may self-service is becoming a per-tenant Encryption Module setting — #1411 —
            // at which point this could be done either way. It is written against today's behaviour.)
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var user = await db.Users.IgnoreQueryFilters(["TenantFilter"])
                .SingleAsync(u => u.TenantId == tenantId && u.NormalizedEmail == email.ToUpperInvariant());
            user.SmimeCertificatePem = pem;
            await db.SaveChangesAsync();
        }

        // …or in the SERVICE's registry, which is where a strict tenant's identities actually live (#1433) and
        // needs no database handle — the provisioning door is HTTP. When both are present the user's own wins,
        // so this overwrites the key the test opens with only when the row is empty.
        if (withRegistryCertificate)
        {
            var registryPkcs12 = _factory.RegisterEncryptionCertificate(email);
            if (!withCertificate)
            {
                pkcs12 = registryPkcs12;
            }
        }

        var repository = (await TestJson.Post(api, "/api/repositories",
            new { name = $"repo-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var documentId = (await TestJson.Post(api, $"/api/documents/{repository}/children",
            new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var versionSelf = await UploadAsync(api, documentId,
            Encoding.ASCII.GetBytes($"{Marker} inside the strict document\n"));

        // Read AFTER the certificate is registered, because that is what the rels depend on — the whole point
        // of the third test is that this same read answers differently without one.
        var version = await TestJson.Get(api, versionSelf);

        return new Reader(api, version,
            Href(version, "download") is { } href && href.Contains("enveloped-content", StringComparison.Ordinal)
                ? href
                : null,
            // The address a stale or hand-built client would use. Composed deliberately, in a test, to prove
            // the route refuses rather than serves — the one place composing is the point (ADR 0543 binds
            // CLIENTS; this is an adversary).
            $"{versionSelf}/enveloped-content",
            pkcs12);
    }

    private static string? Href(JsonElement resource, string rel) =>
        resource.TryGetProperty("links", out var links)
            ? links.EnumerateArray()
                .FirstOrDefault(l => l.GetProperty("rel").GetString() == rel) is { ValueKind: JsonValueKind.Object } link
                ? link.GetProperty("href").GetString()
                : null
            : null;

    /// <summary>A self-signed RSA reader: the PEM they register, and the p12 only they hold.</summary>
    private static (string Pem, byte[] Pkcs12) NewReaderCertificate(string email)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.ExportCertificatePem(), certificate.Export(X509ContentType.Pkcs12, "reader"));
    }

    /// <summary>Uploads content the way a client does, and returns the version's own address.</summary>
    private static async Task<string> UploadAsync(HttpClient api, Guid documentId, byte[] plaintext)
    {
        var version = await TestJson.Post(api, $"/api/documents/{documentId}/versions",
            new { fileExtension = ".txt" });

        // A strict tenant is also an encrypted one, so the upload carries the client-side encryption
        // instruction (ADR 0818) and the bytes are wrapped before the PUT; an ordinary tenant gets no
        // instruction and the same code path uploads plaintext. One helper, both tenants — which is what lets
        // the ordinary-tenant contrast above be the same read against a different mode.
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

        var self = Href(version, "self")!;
        await TestJson.Put(api, self, finalize);
        return self;
    }
}
