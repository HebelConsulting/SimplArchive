using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The strict tier's one property, over the real API: content does not leave as plaintext (#1376, ADR 0825).
//
// A unit test cannot show this. The refusal lives in a storage decorator three layers below the controller,
// and what matters is what an HTTP caller actually receives — a version resource that still describes the
// document, with no door on it. So this drives the real stack and reads the real JSON.
//
// The comparison against an ORDINARY encrypted tenant is the half that makes it mean something: both tenants
// encrypt at rest, and only one of them stops handing out a URL. Without that contrast the test would pass on
// an installation where nothing worked at all.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StrictTierPlaintextDoorTests
{
    private readonly E2EApiFactory _factory;

    public StrictTierPlaintextDoorTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_strict_tenants_version_describes_the_document_and_offers_no_download()
    {
        var resource = await VersionResourceAsync(E2EApiFactory.StrictTenantName);

        // The metadata is still there. That is the whole reason the seam answers null instead of throwing:
        // a refusal that took the resource with it would leave a strict tenant able to see nothing at all.
        Assert.False(string.IsNullOrEmpty(resource.GetProperty("id").GetString()));

        var rels = Rels(resource);
        Assert.DoesNotContain("download", rels);
        Assert.Contains("self", rels);
    }

    [Fact]
    public async Task An_ordinary_encrypted_tenant_still_gets_its_download()
    {
        // The contrast. Both tenants encrypt at rest; only the strict one closes the door — so this is what
        // says the refusal is the TIER's doing rather than the test hitting a broken installation.
        var resource = await VersionResourceAsync(E2EApiFactory.CryptoTenantName);

        Assert.Contains("download", Rels(resource));
    }

    private static HashSet<string> Rels(JsonElement resource) =>
        resource.TryGetProperty("links", out var links)
            ? links.EnumerateArray()
                .Select(l => l.GetProperty("rel").GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : [];

    /// <summary>Uploads a document into a tenant declared with an encryption mode, and reads its version.</summary>
    /// <remarks>
    /// Drives the real B2 upload contract — initiate, client-side AES-GCM under a DEK wrapped against the
    /// published KEK, presigned PUT, finalize — exactly as AtRestEncryptionTests does, because a shortcut
    /// upload would not produce an encrypted object and the door being tested only exists for one.
    /// </remarks>
    private async Task<JsonElement> VersionResourceAsync(string tenantName)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName);

        var email = $"reader-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "rd-1234", "Reader", canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "rd-1234"));

        var repository = (await TestJson.Post(api, "/api/repositories",
            new { name = $"repo-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var documentId = (await TestJson.Post(api, $"/api/documents/{repository}/children",
            new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var version = await TestJson.Post(api, $"/api/documents/{documentId}/versions",
            new { fileName = "secret.txt", contentType = "text/plain" });

        var encryption = version.GetProperty("encryption");
        var kekGeneration = encryption.GetProperty("kekGeneration").GetString()!;

        var plaintext = System.Text.Encoding.ASCII.GetBytes("the bytes a strict tenant will not hand out\n");
        var dek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var blob = new byte[12 + plaintext.Length + 16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using (var aes = new System.Security.Cryptography.AesGcm(dek, 16))
        {
            aes.Encrypt(blob.AsSpan(0, 12), plaintext, blob.AsSpan(12, plaintext.Length), blob.AsSpan(^16..));
        }

        using var kek = System.Security.Cryptography.RSA.Create();
        kek.ImportFromPem(encryption.GetProperty("publicKeyPem").GetString()!);
        var wrappedDek = Convert.ToBase64String(
            kek.Encrypt(dek, System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256));

        using var anonymous = new HttpClient();
        var put = await anonymous.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(blob));
        Assert.True(put.IsSuccessStatusCode, $"presigned PUT answered {(int)put.StatusCode}");

        var self = version.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "self").GetProperty("href").GetString()!;
        await TestJson.Put(api, self, new { wrappedDek, kekGeneration });

        return await TestJson.Get(api, self);
    }
}
