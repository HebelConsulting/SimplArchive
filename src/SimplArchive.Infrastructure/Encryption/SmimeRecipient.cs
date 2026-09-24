using System.Security.Cryptography.X509Certificates;
using MimeKit.Cryptography;

namespace SimplArchive.Infrastructure.Encryption;

/// <summary>
/// Builds the <see cref="CmsRecipient"/> every S/MIME enveloping path in this codebase uses, so the content
/// cipher is stated in ONE place instead of inherited from a library default per call site.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stating the preference is the whole point.</b> MimeKit picks the strongest algorithm the recipient's
/// S/MIME capabilities advertise, and falls back to <b>3DES</b> when it knows none — which is always, because
/// this codebase envelopes to a bare certificate that carries no capabilities attribute. Measured:
/// <c>CmsRecipient(certificate)</c> alone produced content algorithm <c>1.2.840.113549.3.7</c> (des-EDE3-CBC,
/// 8-byte IV, 24-byte key); with the list below it produced <c>2.16.840.1.101.3.4.1.42</c>
/// (aes-256-CBC, 16-byte IV).
/// </para>
/// <para>
/// 3DES is a working cipher, not a broken one, which is exactly why nothing complained: every envelope
/// decrypted correctly and no test could tell. But it carries ~112 bits of effective strength against a
/// meet-in-the-middle attack, NIST disallowed it for new protection after 2023, and the block size is 64 bits
/// — so it is the wrong floor for a system whose IMAP stream and notification mail carry archived documents.
/// </para>
/// <para>
/// The fallback is ordered, not a single choice: a recipient whose certificate DOES advertise capabilities
/// still negotiates, and 3DES remains available at the end for a client that can do nothing else. Removing it
/// would trade a weak envelope for no envelope, which is the worse failure — the enveloper family's whole
/// contract is that it falls back to plaintext only when it cannot encrypt at all.
/// </para>
/// </remarks>
public static class SmimeRecipient
{
    /// <summary>The content ciphers this installation is willing to envelope with, strongest first.</summary>
    public static CmsRecipient For(X509Certificate2 certificate) => new(certificate)
    {
        EncryptionAlgorithms =
        [
            EncryptionAlgorithm.Aes256,
            EncryptionAlgorithm.Aes192,
            EncryptionAlgorithm.Aes128,

            // Last, and only for a client that can do nothing better. See the remarks: a weak envelope beats
            // no envelope, because the alternative this code falls back to is PLAINTEXT.
            EncryptionAlgorithm.TripleDes,
        ],
    };
}
