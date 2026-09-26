using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using MimeKit.Cryptography;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Who a CMS envelope is addressed to — answerable without touching a single private key (#1353, ADR 0832).
/// </summary>
/// <remarks>
/// <para>
/// A CMS <c>EnvelopedData</c> names each recipient by <b>issuer and serial number</b>, in the clear. So
/// <i>"is this document mine?"</i> is a question about public data, and every opener can answer it before
/// asking the platform — or the user — for anything.
/// </para>
/// <para>
/// <b>This is a correctness and consent question, not a speed one.</b> Both openers used to reach for a key
/// first and find out afterwards, and both paid for it in a prompt the user had no reason to see:
/// </para>
/// <list type="bullet">
/// <item>the software opener imported every certificate in the store, and MimeKit needs the PRIVATE KEY to
/// build a decryptor — so on macOS a document addressed to a <i>card</i> raised keychain authorization
/// prompts for unrelated software certificates before the card was ever consulted;</item>
/// <item>the card opener opened a logged-in session, which means a PIN PROMPT, before establishing that the
/// card was the addressee at all — so a document addressed to a colleague asked for the user's PIN and then
/// failed.</item>
/// </list>
/// <para>
/// Asking the envelope first removes both. A credential prompt should mean <i>"this is yours, unlock it"</i>,
/// and a prompt that appears when the answer is going to be no teaches people to click through prompts.
/// </para>
/// </remarks>
public static class EnvelopeRecipients
{
    /// <summary>The issuer/serial pairs this envelope is addressed to, or empty if it cannot be read.</summary>
    public static IReadOnlyList<(string Issuer, string Serial)> Of(ApplicationPkcs7Mime enveloped)
    {
        if (enveloped.Content is null)
        {
            return [];
        }

        try
        {
            using var cms = new MemoryStream();
            enveloped.Content.DecodeTo(cms);
            var envelopedCms = new EnvelopedCms();
            envelopedCms.Decode(cms.ToArray());

            var recipients = new List<(string, string)>();
            foreach (RecipientInfo recipient in envelopedCms.RecipientInfos)
            {
                // IssuerAndSerialNumber is what MimeKit's CmsRecipient produces and what the server sends.
                // A SubjectKeyIdentifier recipient is legal CMS and simply is not matched here — it falls
                // through to "not addressed to anything I hold", which is the safe direction: an opener that
                // guessed would be back to prompting for keys that cannot help.
                if (recipient.RecipientIdentifier.Value is X509IssuerSerial id)
                {
                    recipients.Add((id.IssuerName, id.SerialNumber));
                }
            }

            return recipients;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException)
        {
            // Unreadable here is not a failure: the opener that owns this envelope will refuse with its own
            // message, which is better placed to say what went wrong.
            return [];
        }
    }

    /// <summary>The subset of <paramref name="held"/> the envelope's <paramref name="recipients"/> name.</summary>
    /// <remarks>
    /// Takes the already-read recipient list rather than the envelope: the funnel reads it once and hands it to
    /// every opener, so nothing here decodes the same CMS a second time.
    /// </remarks>
    public static IReadOnlyList<X509Certificate2> AddressedTo(
        IReadOnlyList<(string Issuer, string Serial)> recipients, X509Certificate2Collection held) =>
        held.Count == 0 || recipients.Count == 0
            ? []
            : held.Cast<X509Certificate2>().Where(c => Matches(c, recipients)).ToList();

    /// <summary>Whether this certificate is one of the envelope's recipients.</summary>
    /// <remarks>
    /// The serial number is compared case-insensitively because it is a hex string whose casing is the
    /// producer's choice, and the issuer name as an ordinal string because both sides render the same
    /// distinguished name through the same formatter.
    /// </remarks>
    public static bool Matches(X509Certificate2 certificate, IReadOnlyList<(string Issuer, string Serial)> recipients) =>
        recipients.Any(r =>
            string.Equals(r.Serial, certificate.SerialNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Issuer, certificate.Issuer, StringComparison.Ordinal));
}
