using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using CAManagement.Pkcs11.DataStructures;
using MimeKit;
using MimeKit.Cryptography;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Opens a CMS envelope with the key on a card, which never leaves it (#1353, ADR 0832).
/// </summary>
/// <remarks>
/// <para>
/// The card performs exactly ONE operation: <c>C_Decrypt(CKM_RSA_PKCS)</c> over the 256-byte encrypted
/// content-encryption key. Everything else — finding the recipient, decrypting the content, parsing the
/// document back out — happens here in software, over a key that existed for microseconds.
/// </para>
/// <para>
/// <b>PKCS#1 v1.5, not OAEP.</b> CMS key transport is <c>rsaEncryption</c>, and the mechanism is therefore
/// <c>CKM_RSA_PKCS</c>. <c>DecryptRsaOaep</c> is the wrong primitive here and is the one that looks right.
/// </para>
/// <para>
/// <b>The IV is PARSED, not hunted.</b> .NET's <c>EnvelopedCms.ContentEncryptionAlgorithm.Parameters</c> comes
/// back EMPTY for AES-CBC — measured, repeatedly — so the initialisation vector has to be read from the CMS
/// itself. The first working version found it by scanning the DER for the AES OID and stepping over the two
/// bytes after it, which happens to work and would fail silently the first time anything about the encoding
/// moved. This reads the structure instead.
/// </para>
/// </remarks>
public static class CardEnvelopeOpener
{
    /// <summary>Opens the envelope with the card, or declines when the card cannot be the recipient.</summary>
    public static async Task<(byte[] Bytes, string ContentType)?> TryOpen(
        ApplicationPkcs7Mime enveloped, IReadOnlyList<(string Issuer, string Serial)> recipients)
    {
        // IS THIS CARD EVEN THE ADDRESSEE? Asked first, and asked from PUBLIC data — the envelope names its
        // recipients by issuer and serial, and a token's certificates are readable with no login. Only then is
        // a session opened, which is what prompts for the PIN.
        //
        // The order is the whole point. Opening the session first meant a document addressed to a COLLEAGUE
        // asked for the user's PIN and then failed — a credential prompt whose answer was going to be no,
        // which is exactly how people learn to click through prompts.
        if (recipients.Count == 0 || enveloped.Content is null)
        {
            return null;
        }

        using var cmsStream = new MemoryStream();
        enveloped.Content.DecodeTo(cmsStream);
        var raw = cmsStream.ToArray();

        // ONE gated critical section for everything that touches the card: reading its certificates, opening
        // the logged-in session, and the unwrap. Not three, because C_Initialize is per PROCESS and a decrypt
        // is two calls with state between them — a preview firing its page render, thumbnails and text layout
        // at once is exactly what found that out (CKR_CRYPTOKI_ALREADY_INITIALIZED, live).
        return await CardModule.UseAsync<(byte[] Bytes, string ContentType)?>(
            async library =>
            {
                // IS THIS CARD EVEN THE ADDRESSEE? From PUBLIC data — the envelope names its recipients and a
                // token's certificates are readable with no login — so the PIN is only ever asked for a
                // document this card can actually open. A prompt whose answer is going to be no is how people
                // learn to click through prompts.
                var onCard = CardCertificates.ReadFromLibrary(library);
                CardModule.Observed(onCard.Count > 0);

                if (!onCard.Any(c => EnvelopeRecipients.Matches(c.Certificate, recipients)))
                {
                    return null;
                }

                if (await CardSession.OpenAsync(library) is not { } session)
                {
                    return null;
                }

                return Unwrap(session, raw);
            },
            whenUnavailable: null);
    }

    private static (byte[] Bytes, string ContentType)? Unwrap(
        CAManagement.Pkcs11.Pkcs11Session session, byte[] raw)
    {

        var envelopedCms = new EnvelopedCms();
        try
        {
            envelopedCms.Decode(raw);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (envelopedCms.RecipientInfos.Count == 0)
        {
            return null;
        }

        byte[] contentKey;
        try
        {
            // The first (and, for this server, only) recipient. A multi-recipient envelope would want the one
            // matching the card's own certificate; the server addresses one reader per response, so taking the
            // first is correct here and would become a lookup the day that changes.
            var keys = session.FindObjects(CK_OBJECT_CLASS.CKO_PRIVATE_KEY);
            if (keys.Count == 0)
            {
                return null;
            }

            contentKey = session.Decrypt(
                CK_MECHANISM_TYPE.CKM_RSA_PKCS, envelopedCms.RecipientInfos[0].EncryptedKey, keys[0]);
        }
        catch (Exception e)
        {
            // The card is present and holds a key, and it still could not unwrap this — which means the
            // envelope was addressed to somebody else. DECLINING lets a later opener try; with the card last
            // in the list, the caller then gets the "which of the three is missing" message.
            DesktopLog.Debug("The card declined to unwrap this envelope: {Reason}", e.Message);
            return null;
        }

        var (cipherOid, iv) = ReadContentCipher(raw);
        if (cipherOid != AesCbcOid)
        {
            // Anything else is a cipher this client was never told about. Refusing names the reason; guessing
            // would produce bytes that look like a corrupt document.
            throw new EnvelopeNotOpenedException(
                $"the document uses a content cipher this client does not implement ({cipherOid})");
        }

        using var aes = Aes.Create();
        aes.Key = contentKey;
        aes.IV = iv;
        var inner = aes.CreateDecryptor().TransformFinalBlock(
            envelopedCms.ContentInfo.Content, 0, envelopedCms.ContentInfo.Content.Length);

        // What comes out is the inner MIME entity the server enveloped — a part with the document's real
        // content type and file name, which is the whole reason the funnel returns a type at all.
        if (MimeEntity.Load(new MemoryStream(inner)) is not MimePart part || part.Content is null)
        {
            throw new EnvelopeNotOpenedException("the envelope opened but held no document");
        }

        using var opened = new MemoryStream();
        part.Content.DecodeTo(opened);
        return (opened.ToArray(), part.ContentType?.MimeType ?? "application/octet-stream");
    }

    /// <summary>Which of card, reader or registered certificate is missing — the message the reader acts on.</summary>
    /// <remarks>
    /// #1353's acceptance point 3, and the reason it is an acceptance criterion: one generic failure sends
    /// three different people to three wrong places — install software, find your card, or ask an
    /// administrator. Whichever is missing, the sentence has to name it.
    /// </remarks>
    public static string WhatIsMissing() =>
        Missing(CardCertificates.FindModule() is not null, CardModule.LastSawToken ?? false);

    /// <summary>The message itself, as a pure function of what was found — so every branch is testable.</summary>
    /// <remarks>
    /// Separated from the probing deliberately: the probing answers differently on every machine (a developer
    /// with a reader, a CI runner with none), so a test over the combined thing would assert whatever the host
    /// happened to have. Splitting it is what makes all three sentences checkable anywhere.
    /// </remarks>
    public static string Missing(bool smartcardSoftware, bool cardPresent) => (smartcardSoftware, cardPresent) switch
    {
        (false, _) => "no certificate on this computer holds the key it was addressed to, and no smartcard "
            + "software is installed to reach one on a card",
        (true, false) => "no certificate on this computer holds the key it was addressed to, and no card or "
            + "token is in a reader",
        _ => "neither this computer nor the card in the reader holds the key it was addressed to",
    };

    private const string AesCbcOid = "2.16.840.1.101.3.4.1.42";

    /// <summary>
    /// The content cipher and its initialisation vector, read out of the CMS structure.
    /// </summary>
    /// <remarks>
    /// <code>
    /// ContentInfo          ::= SEQUENCE { contentType OID, [0] EXPLICIT EnvelopedData }
    /// EnvelopedData        ::= SEQUENCE { version, [0] originatorInfo OPTIONAL, recipientInfos SET,
    ///                                     encryptedContentInfo EncryptedContentInfo, ... }
    /// EncryptedContentInfo ::= SEQUENCE { contentType OID, contentEncryptionAlgorithm AlgorithmIdentifier,
    ///                                     [0] IMPLICIT encryptedContent OCTET STRING OPTIONAL }
    /// AlgorithmIdentifier  ::= SEQUENCE { algorithm OID, parameters ANY }  -- for AES-CBC, an OCTET STRING IV
    /// </code>
    /// BER rather than DER, because that is what CMS permits and what a producer may emit.
    /// </remarks>
    internal static (string CipherOid, byte[] Iv) ReadContentCipher(byte[] cms)
    {
        try
        {
            var contentInfo = new AsnReader(cms, AsnEncodingRules.BER).ReadSequence();
            contentInfo.ReadObjectIdentifier();
            var envelopedData = contentInfo
                .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
                .ReadSequence();

            envelopedData.ReadInteger();
            if (envelopedData.PeekTag().TagClass == TagClass.ContextSpecific)
            {
                envelopedData.ReadEncodedValue(); // originatorInfo, which this server does not send
            }

            envelopedData.ReadSetOf();
            var encryptedContentInfo = envelopedData.ReadSequence();
            encryptedContentInfo.ReadObjectIdentifier();

            var algorithm = encryptedContentInfo.ReadSequence();
            return (algorithm.ReadObjectIdentifier(), algorithm.ReadOctetString());
        }
        catch (AsnContentException e)
        {
            throw new EnvelopeNotOpenedException("its encrypted structure could not be read", e);
        }
    }
}
