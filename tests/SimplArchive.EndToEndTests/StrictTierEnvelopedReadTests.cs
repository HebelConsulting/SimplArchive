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
    public async Task The_find_overlay_rel_is_absent_where_the_route_refuses_and_present_where_it_serves()
    {
        // ADVERTISING AND ACCEPTING HAVE TO BE THE SAME LIST (#1402). The overlay's word coordinates
        // reconstruct the document, so the route is refused on a tenant whose doors refuse plaintext — while
        // the rel was advertised on EVERY version regardless.
        //
        // This one lied in the worst available way rather than merely failing: the endpoint answers 204 for a
        // format with no overlay, which a client reads as "nothing to highlight". A client that followed the
        // rel and met the refusal could not tell "this document has no text" from "this tenant will not tell
        // you", so find-in-document looked BROKEN rather than relocated (it is computed client-side from the
        // decrypted bytes, ADR 0830).
        var strict = await StrictReaderAsync(withCertificate: true);

        Assert.Null(Href(strict.Version, "text-layout"));

        using var forced = await strict.Api.GetAsync(strict.ForcedTextLayoutHref);
        Assert.Equal(HttpStatusCode.Conflict, forced.StatusCode);

        // The contrast, without which the assertion above would pass on an installation where the overlay is
        // simply gone for everybody.
        var ordinary = await ReaderAsync($"Plain{Guid.NewGuid():N}"[..24], withCertificate: false);

        Assert.NotNull(Href(ordinary.Version, "text-layout"));
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

    // (The two #1433 cases that lived here — a certificate held by the encryption service's REGISTRY is found, and
    // the user's own column wins over it — tested a source that is retired (ADR 0890). A reader's certificates
    // come from the Module or the reader's own column; the column is exercised throughout this class.)

    /// <summary>
    /// A strict tenant must say a preview is CONVERTED when it is — the same answer an ordinary tenant gives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>previewConverted</c> was assigned in exactly one place, the non-enveloped branch, so for a reader with
    /// a certificate it kept its <c>false</c> default and the "converted preview" badge could never appear on a
    /// strict tenant, whatever the format (#1454).
    /// </para>
    /// <para>
    /// That is not strict serving originals: an inline enveloped read resolves the DISPLAY object (ADR 0828)
    /// exactly as the plaintext path does, so the envelope carries the rendition. The reader was simply never
    /// told — and the badge's absence reads as "this is the original", the one thing it exists to deny.
    /// </para>
    /// <para>
    /// Written as an AGREEMENT between the two tiers rather than as "strict says true", because the property
    /// that matters is that the answer does not depend on the tier.
    /// </para>
    /// <para>
    /// <b>One case per CONVERTER, not one per path</b> (#1529, split out of #1378). Markdown alone proved the
    /// path; it could not prove that each converter is reached on the enveloped branch — and the defect this
    /// area actually produced was of exactly that shape: <c>previewConverted</c> assigned in one place, the
    /// non-enveloped branch, so the badge could never appear whatever the format (#1454). A per-format version
    /// of that flaw is invisible in the same way. So the four subjects are the four distinct routes a display
    /// object can come from: markdown and e-mail through the conversion sidecar's browser route, an office
    /// document through its LibreOffice route, and a TIFF through the image library in-process.
    /// </para>
    /// <para>
    /// The <c>.docx</c> is a real OOXML package built here rather than the <c>.csv</c> stand-in
    /// <c>PreviewRenditionTests</c> uses for the office family, because that would make this the same claim
    /// again under a new name — and because nothing else proves a VALID <c>.docx</c> converts at all: the only
    /// other one in the suite is deliberately corrupt, pinning the failure path.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(".md")]
    [InlineData(".docx")]
    [InlineData(".eml")]
    [InlineData(".tif")]
    public async Task A_converted_preview_is_reported_as_converted_on_both_tiers(string fileExtension)
    {
        var sample = ConvertibleSample(fileExtension);

        var strict = await ConvertedFlagAsync(
            E2EApiFactory.StrictTenantName, withCertificate: true, sample, fileExtension);
        var ordinary = await ConvertedFlagAsync(
            E2EApiFactory.CryptoTenantName, withCertificate: false, sample, fileExtension);

        // The ordinary tenant first, and it is not ceremony: it is what makes a failure DIAGNOSABLE. Ordinary
        // true with strict false points at the enveloped branch; both false points at the converter or its
        // sidecar. Asserting strict alone would still fail, but would not say which of the two to go and read.
        Assert.True(ordinary, $"the ordinary tenant should report a {fileExtension} preview as converted — if "
            + "this fails the fixture is not converting this format at all, and the strict assertion below "
            + "would be measuring the converter rather than the tier");
        Assert.True(strict, $"a strict tenant delivers the RENDITION for an inline read of {fileExtension} "
            + "(ADR 0828) and must say so, or the reader is shown a converted document with nothing saying it "
            + "is not the original");
    }

    /// <summary>A smallest-possible document of <paramref name="fileExtension"/> that the server converts.</summary>
    /// <remarks>
    /// Synthesized rather than committed: an opaque binary under <c>tests/</c> is one nobody can review or
    /// regenerate, and that directory is published byte-for-byte (ADR 0484). The TIFF goes through NetVips,
    /// which is how this suite already makes TIFFs — the managed library is MIT, and the LGPL native it binds
    /// is the project's one documented licence exception, already referenced by this test project for the
    /// platforms tests run on (issue #496 carries the trigger that retires those).
    /// </remarks>
    private static byte[] ConvertibleSample(string fileExtension) => fileExtension switch
    {
        ".md" => Encoding.ASCII.GetBytes("# Heading\n\nA markdown document the server renders to PDF.\n"),
        ".docx" => MinimalOfficeDocument(),
        ".eml" => Encoding.ASCII.GetBytes(
            "From: sender@e2e.local\r\nTo: reader@e2e.local\r\nSubject: A message the archive renders\r\n"
            + "Date: Thu, 1 Jan 2026 09:50:00 +0000\r\nMIME-Version: 1.0\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n\r\nA message body the server renders to PDF.\r\n"),
        ".tif" => (NetVips.Image.Black(600, 800) + 255)
            .Cast(NetVips.Enums.BandFormat.Uchar)
            .WriteToBuffer(".tif"),
        _ => throw new ArgumentOutOfRangeException(nameof(fileExtension), fileExtension, "no sample for this"),
    };

    /// <summary>The smallest valid OOXML word-processing package: content types, the relationship, the body.</summary>
    /// <remarks>
    /// Three parts is genuinely the minimum — drop <c>_rels/.rels</c> and the import filter cannot find the
    /// main document part, which fails as a CONVERSION failure (no <c>preview</c> link) rather than as
    /// anything naming the package, so a shortcut here would read exactly like the defect under test.
    /// </remarks>
    private static byte[] MinimalOfficeDocument()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);

            Write(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Target="word/document.xml" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"/>
                </Relationships>
                """);

            Write(zip, "word/document.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>An office document the server renders to PDF.</w:t></w:r></w:p></w:body>
                </w:document>
                """);
        }

        return buffer.ToArray();

        static void Write(System.IO.Compression.ZipArchive zip, string path, string xml)
        {
            using var entry = zip.CreateEntry(path).Open();
            entry.Write(Encoding.UTF8.GetBytes(xml));
        }
    }

    /// <summary>Uploads one document to a tenant and reports what its version says about conversion.</summary>
    private async Task<bool> ConvertedFlagAsync(
        string tenantName, bool withCertificate, byte[] sample, string fileExtension)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName);
        var email = $"converted-reader-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "cr-1234", "Converted Reader", canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "cr-1234"));

        if (withCertificate)
        {
            // Through the module on the Strict tenant, for the reason the helper above records (ADR 0890).
            var (pem, _) = NewReaderCertificate(email);
            await _factory.EnrolReaderCertificateAsync(tenantId, userId, pem);
        }

        var repository = (await TestJson.Post(api, "/api/repositories",
            new { name = $"repo-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var documentId = (await TestJson.Post(api, $"/api/documents/{repository}/children",
            new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var versionSelf = await UploadAsync(api, documentId, sample, fileExtension);

        var version = await TestJson.Get(api, versionSelf);
        return version.GetProperty("previewConverted").GetBoolean();
    }

    private sealed record Reader(
        HttpClient Api, JsonElement Version, string? EnvelopeHrefOrNull, string ForcedEnvelopeHref,
        string ForcedTextLayoutHref, byte[] Pkcs12)
    {
        public string EnvelopeHref => EnvelopeHrefOrNull
            ?? throw new InvalidOperationException("the version advertises no enveloped-content address");
    }

    private Task<Reader> StrictReaderAsync(bool withCertificate) =>
        ReaderAsync(E2EApiFactory.StrictTenantName, withCertificate);

    /// <summary>A signed-in reader in a tenant, a document with content, and that version's addresses.</summary>
    private async Task<Reader> ReaderAsync(
        string tenantName, bool withCertificate)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName);

        var email = $"strict-reader-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "sr-1234", "Strict Reader", canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "sr-1234"));

        var (pem, pkcs12) = NewReaderCertificate(email);
        if (withCertificate)
        {
            // ENROLLED THROUGH THE MODULE, the only source a Strict tenant's readers have since the service's
            // registry was retired (ADR 0890): the factory activated the TestModule for this tenant, and its
            // enrolment route files the certificate as the module's own document. Self-service is closed here
            // (ADR 0813), and the user row is a source only where no module speaks.
            await _factory.EnrolReaderCertificateAsync(tenantId, userId, pem);
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
            $"{versionSelf}/text-layout",
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
    private static async Task<string> UploadAsync(
        HttpClient api, Guid documentId, byte[] plaintext, string fileExtension = ".txt")
    {
        var version = await TestJson.Post(api, $"/api/documents/{documentId}/versions",
            new { fileExtension });

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
