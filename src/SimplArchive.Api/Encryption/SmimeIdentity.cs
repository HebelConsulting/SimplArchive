using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SimplArchive.Api.Encryption;

/// <summary>
/// Builds the self-service S/MIME identity (#1332): a SELF-SIGNED certificate with exactly the profile
/// S/MIME encryption requires — keyEncipherment (RSA S/MIME wraps the content key with the recipient's
/// key), the emailProtection EKU, and the address as an rfc822Name SAN so a mail client matches the
/// certificate to the account. Self-signed by design: reading encrypted mail needs only the installed
/// identity, and chain semantics belong to the encryption service's externally-provisioned certificates.
/// The private key exists in memory for the duration of one request and is never persisted.
/// </summary>
public static class SmimeIdentity
{
    private const string EmailProtectionOid = "1.3.6.1.5.5.7.3.4";

    public sealed record Generated(string CertificatePem, byte[] Pkcs12, string MobileConfig);

    /// <summary>ingestCertificatePem (#1335): the tenant's mail-ingest certificate rides the profile as a
    /// second payload, so composing TO the archive's ingest address offers encryption with zero setup —
    /// iOS consults profile-installed certificates for S/MIME recipient lookup.</summary>
    public static Generated Generate(string email, string pkcs12Password, TimeSpan lifetime, string? ingestCertificatePem = null)
    {
        using var key = RSA.Create(3072);
        using var certificate = SelfSign(email, key, lifetime);

        var pkcs12 = certificate.Export(X509ContentType.Pkcs12, pkcs12Password);
        return new Generated(
            certificate.ExportCertificatePem(),
            pkcs12,
            BuildMobileConfig(email, pkcs12, pkcs12Password, ingestCertificatePem));
    }

    /// <summary>The S/MIME certificate profile for <paramref name="email"/>, self-signed over
    /// <paramref name="key"/> — shared by the user identity above and the tenant ingest key (#1335),
    /// because two hand-rolled copies of one certificate profile is how they drift apart.</summary>
    public static X509Certificate2 SelfSign(string email, RSA key, TimeSpan lifetime)
    {
        var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(EmailProtectionOid)], critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress(email);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);   // clock-skew grace, the usual issuance courtesy
        return request.CreateSelfSigned(notBefore, notBefore + lifetime);
    }

    /// <summary>
    /// The iOS configuration profile carrying the identity — one payload, the PKCS#12 (the platform-CA
    /// payload of the sidecar's identity tool has no counterpart here: there is no CA). The password rides
    /// inside so the install does not prompt — acceptable because the profile is downloaded over the
    /// user's own authenticated session and never stored server-side.
    /// </summary>
    private static string BuildMobileConfig(string email, byte[] pkcs12, string pkcs12Password, string? ingestCertificatePem)
    {
        var identityUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var profileUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var ingestPayload = string.Empty;
        if (ingestCertificatePem is { Length: > 0 })
        {
            using var ingest = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(ingestCertificatePem);
            ingestPayload = $"""
                    <dict>
                        <key>PayloadType</key><string>com.apple.security.root</string>
                        <key>PayloadVersion</key><integer>1</integer>
                        <key>PayloadIdentifier</key><string>dev.simplarchive.ingest.{email}</string>
                        <key>PayloadUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                        <key>PayloadDisplayName</key><string>SimplArchive mail-in certificate</string>
                        <key>PayloadContent</key>
                        <data>{Convert.ToBase64String(ingest.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert))}</data>
                    </dict>

            """;
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>PayloadContent</key>
                <array>
                    <dict>
                        <key>PayloadType</key><string>com.apple.security.pkcs12</string>
                        <key>PayloadVersion</key><integer>1</integer>
                        <key>PayloadIdentifier</key><string>dev.simplarchive.identity.{email}</string>
                        <key>PayloadUUID</key><string>{identityUuid}</string>
                        <key>PayloadDisplayName</key><string>SimplArchive S/MIME identity ({email})</string>
                        <key>Password</key><string>{System.Security.SecurityElement.Escape(pkcs12Password)}</string>
                        <key>PayloadContent</key>
                        <data>{Convert.ToBase64String(pkcs12)}</data>
                    </dict>
            {ingestPayload}    </array>
                <key>PayloadType</key><string>Configuration</string>
                <key>PayloadVersion</key><integer>1</integer>
                <key>PayloadIdentifier</key><string>dev.simplarchive.smime.{email}</string>
                <key>PayloadUUID</key><string>{profileUuid}</string>
                <key>PayloadDisplayName</key><string>SimplArchive encrypted mail ({email})</string>
            </dict>
            </plist>
            """;
    }
}
