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
    /// <b>RSA or EC, and the RSA-only version of this was a real outage (#1498).</b> This asked only for an
    /// RSA key, refusing anything else with <i>"S/MIME key transport needs an RSA key"</i> — a premise that
    /// stopped being true: CMS addresses an EC recipient by <b>key agreement</b>, the encryption service
    /// produces such an envelope (its ADR 0018) and the desktop client has opened one since ADR 0830, with
    /// the agreement performed on the card. A PIV card's key-management slot normally holds an EC key, so
    /// the one certificate kind the product exists to serve was the one this refused.
    /// </para>
    /// <para>
    /// And it failed in the worst available direction. The refusal is swallowed by
    /// <c>StrictEnvelopeDelivery.Usable</c> — deliberately, so an unreadable certificate makes the rel
    /// disappear rather than fail on click — so the reader's certificate set came back EMPTY, the strict
    /// tier's <c>download</c>/<c>preview</c> rels fell back to a presigned storage URL, and the client
    /// fetched bytes it could not recognise. Measured: the desktop said <i>"Preview not supported"</i> and
    /// never asked for the card's PIN, because no envelope was ever built.
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
            // Both recipient kinds CMS can address, asked in the same way — load the key or refuse. RSA is
            // key transport, EC is key agreement; which one this is decides how the envelope is built, and
            // neither this method nor its callers have to know, because the enveloper asks the same question
            // of the same certificate.
            using var rsa = certificate.GetRSAPublicKey();
            using var ecdsa = certificate.GetECDsaPublicKey();
            using var ecdh = certificate.GetECDiffieHellmanPublicKey();
            if (rsa is null && ecdsa is null && ecdh is null)
            {
                throw new InvalidRecipientCertificateException(
                    "its public key is neither RSA nor elliptic-curve, so no CMS envelope can be addressed "
                    + "to it");
            }

            return new Described(
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                // The subject as written, so an auditor reads what the issuer wrote rather than our
                // interpretation of it. Empty is possible (a certificate identified only by its SAN), and an
                // empty string is the honest answer there — the fingerprint still identifies the key.
                certificate.Subject);
        }
    }
}
