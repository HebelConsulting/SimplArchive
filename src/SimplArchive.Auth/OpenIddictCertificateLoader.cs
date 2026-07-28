using System.Security.Cryptography.X509Certificates;

namespace SimplArchive.Auth;

// Builds the OpenIddict signing/encryption certificates from PEM material sourced from OpenBao (ADR "OpenIddict
// certificates from OpenBao"). A bare X509Certificate2.CreateFromPem carries an ephemeral private key that some
// crypto paths reject, so a PKCS#12 in-memory round-trip is used to produce a certificate whose private key is
// reliably usable for token signing across platforms.
public static class OpenIddictCertificateLoader
{
    public static X509Certificate2 FromPem(string certificatePem, string privateKeyPem)
    {
        using var fromPem = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        var pfx = fromPem.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }
}
