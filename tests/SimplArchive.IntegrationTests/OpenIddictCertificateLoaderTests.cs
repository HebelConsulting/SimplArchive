using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SimplArchive.Auth;

namespace SimplArchive.IntegrationTests;

// Verifies OpenIddictCertificateLoader.FromPem (ADR "OpenIddict certificates from OpenBao") produces a
// certificate whose private key is actually usable for signing — the whole point (a bare CreateFromPem cert can
// carry an ephemeral key that signing paths reject).
public class OpenIddictCertificateLoaderTests
{
    [Fact]
    public void Loads_a_certificate_from_pem_with_a_usable_private_key()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=simplarchive-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var original = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        var certificatePem = original.ExportCertificatePem();
        var privateKeyPem = rsa.ExportRSAPrivateKeyPem(); // PKCS#1 "BEGIN RSA PRIVATE KEY", as OpenBao PKI issues

        using var loaded = OpenIddictCertificateLoader.FromPem(certificatePem, privateKeyPem);

        Assert.True(loaded.HasPrivateKey);

        // Sign with the loaded private key and verify with its public key — proves the key round-tripped intact.
        var payload = Encoding.UTF8.GetBytes("token-to-sign");
        using var signingKey = loaded.GetRSAPrivateKey();
        Assert.NotNull(signingKey);
        var signature = signingKey!.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var verifyKey = loaded.GetRSAPublicKey();
        Assert.NotNull(verifyKey);
        Assert.True(verifyKey!.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
