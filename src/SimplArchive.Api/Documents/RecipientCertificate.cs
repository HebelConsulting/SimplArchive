using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.Api.Errors.Exceptions.ExternalLinks;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Reads the certificate an external link is addressed to, and says who that is (#1377, ADR 0827).
/// </summary>
/// <remarks>
/// Its own type rather than a few lines in the controller, because it answers two questions that must agree:
/// <b>may this certificate be used?</b> and <b>who was this shared with?</b> — the second being the audit
/// record's only content, and the first being what makes the second true. Splitting them across two places is
/// how a link comes to be audited as addressed to a certificate it was then refused for.
/// </remarks>
public static class RecipientCertificate
{
    /// <summary>What the audit names: the exact key, and the human-readable holder.</summary>
    public readonly record struct Described(string Fingerprint, string Subject)
    {
        public override string ToString() => $"{Subject} ({Fingerprint})";
    }

    /// <summary>
    /// Validates a PEM and describes it, or throws <see cref="InvalidRecipientCertificateException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The encryption capability is checked by ASKING FOR THE PUBLIC KEY, not by reading Key Usage. A
    /// certificate may carry no Key Usage extension at all and still be perfectly usable — the extension is
    /// optional — so refusing on its absence would reject valid certificates, while trusting its presence
    /// would accept a certificate whose key we cannot actually load. The question that matters is whether an
    /// envelope can be built against this key, and the only honest way to ask it is to try.
    /// </para>
    /// <para>
    /// Deliberately NOT checked: expiry. A certificate that expires next week still encrypts today, and the
    /// private key outlives its certificate — refusing on expiry would break the case where somebody shares
    /// with a long-standing holder whose certificate is due for renewal. Expiry is the recipient's business.
    /// </para>
    /// </remarks>
    public static Described Validate(string pem)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(pem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            throw new InvalidRecipientCertificateException("it is not a readable PEM-encoded X.509 certificate");
        }

        using (certificate)
        {
            if (certificate.GetRSAPublicKey() is not { } key)
            {
                throw new InvalidRecipientCertificateException(
                    "its public key is not RSA, and S/MIME key transport needs an RSA key");
            }

            key.Dispose();

            return new Described(
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                // The subject as written, so an auditor reads what the issuer wrote rather than our
                // interpretation of it. Empty is possible (a certificate identified only by its SAN), and an
                // empty string is the honest answer there — the fingerprint still identifies the key.
                certificate.Subject);
        }
    }
}
