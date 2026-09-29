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

            // ROUTED BY RECIPIENT KIND, because the two kinds do not differ in degree. An RSA recipient has
            // the content key WRAPPED to its public key and carries it in EncryptedKey; an elliptic-curve
            // recipient AGREES a key-encryption key and EncryptedKey holds the content key wrapped under
            // THAT. Reading EncryptedKey and decrypting it is meaningless for the second — which is why this
            // opener could not read an EC recipient of any curve before ABI-side work made agreement possible.
            contentKey = envelopedCms.RecipientInfos[0] switch
            {
                // The card's part passed as a LAMBDA, not the handle: NativeULong is a global using alias
                // inside CAManagement (uint on Windows, ulong on Unix), so a consumer cannot name it in a
                // signature — and closing over it here keeps the agreement logic free of the card entirely,
                // which is what makes it testable without one.
                KeyAgreeRecipientInfo agreed => AgreeContentKey(
                    raw, agreed.EncryptedKey, point => session.DeriveEcdhSecret(point, keys[0])),
                _ => session.Decrypt(
                    CK_MECHANISM_TYPE.CKM_RSA_PKCS, envelopedCms.RecipientInfos[0].EncryptedKey, keys[0]),
            };
        }
        catch (Exception e)
        {
            // The card is present and holds a key, and it still could not unwrap this — which means the
            // envelope was addressed to somebody else. DECLINING lets a later opener try; with the card last
            // in the list, the caller then gets the "which of the three is missing" message.
            DesktopLog.Debug("The card declined to unwrap this envelope: {Reason}", e.Message);
            return null;
        }

        var inner = DecryptContent(raw, envelopedCms.ContentInfo.Content, contentKey);

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

    /// <summary>
    /// Agrees the content key with <paramref name="deriveSharedSecret"/> and returns the DECRYPTED content —
    /// the whole key-agreement path, with no card and no MIME parsing in it.
    /// </summary>
    /// <remarks>
    /// Split out so the hardware check (<c>--card-envelope-test</c>) exercises exactly what production does.
    /// It stops before the MIME step on purpose: this server wraps an inner MIME entity, but an envelope from
    /// another producer need not, and the point of the check is the CRYPTO agreeing with a standard
    /// implementation rather than our own framing.
    /// </remarks>
    internal static byte[] OpenWithAgreement(byte[] raw, Func<byte[], byte[]> deriveSharedSecret)
    {
        var envelopedCms = new EnvelopedCms();
        envelopedCms.Decode(raw);

        var contentKey = AgreeContentKey(
            raw, envelopedCms.RecipientInfos[0].EncryptedKey, deriveSharedSecret);

        return DecryptContent(raw, envelopedCms.ContentInfo.Content, contentKey);
    }

    /// <summary>The content, decrypted under its own cipher and IV.</summary>
    private static byte[] DecryptContent(byte[] raw, byte[] encryptedContent, byte[] contentKey)
    {
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
        return aes.CreateDecryptor().TransformFinalBlock(encryptedContent, 0, encryptedContent.Length);
    }

    /// <summary>AES-256 key wrap — the key-encryption algorithm an EC recipient's content key is wrapped
    /// under (RFC 3565). Named so the refusal below can say what it did not recognise.</summary>
    private const string Aes256WrapOid = "2.16.840.1.101.3.4.1.45";

    private const string Aes128WrapOid = "2.16.840.1.101.3.4.1.5";

    private const string Aes192WrapOid = "2.16.840.1.101.3.4.1.25";

    /// <summary>
    /// The KDF scheme OIDs, which name the HASH the sender's X9.63 derivation used (RFC 5753 §7.1.4).
    /// </summary>
    /// <remarks>
    /// <b>There is no sensible default here, and assuming one is the mistake this table exists to stop.</b>
    /// OpenSSL emits <c>dhSinglePass-stdDH-sha1kdf-scheme</c> unless told otherwise — so an implementation that
    /// assumed SHA-256 would derive the wrong key against the most likely interoperating producer there is, and
    /// the only symptom would be a failed integrity check with nothing to point at.
    ///
    /// <para>
    /// COFACTOR variants are deliberately absent. For a prime curve of cofactor 1 the arithmetic is the same,
    /// but the PKCS#11 mechanism is not (<c>CKM_ECDH1_COFACTOR_DERIVE</c>), and a card may implement one and
    /// not the other — measured: <c>pkcs11-tool</c>'s default of the cofactor mechanism is rejected outright
    /// for a Montgomery key. Refusing by name beats deriving with the wrong mechanism.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, HashAlgorithmName> KdfSchemes = new(StringComparer.Ordinal)
    {
        ["1.3.133.16.840.63.0.2"] = HashAlgorithmName.SHA1,      // dhSinglePass-stdDH-sha1kdf-scheme
        // sha224kdf (1.3.132.1.11.0) is absent because .NET has no SHA-224 at all — listing it would be a
        // promise the runtime cannot keep, and the refusal above names the OID either way.
        ["1.3.132.1.11.1"] = HashAlgorithmName.SHA256,
        ["1.3.132.1.11.2"] = HashAlgorithmName.SHA384,
        ["1.3.132.1.11.3"] = HashAlgorithmName.SHA512,
    };

    /// <summary>
    /// The content-encryption key of an elliptic-curve recipient: agree a shared secret on the card, derive a
    /// key-encryption key from it, and unwrap the content key with that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps, and every one of them has to agree with what the sender did or the unwrap's integrity
    /// check fails — which is the design's saving grace: a mistake anywhere here is loud rather than silently
    /// producing a document full of noise.
    /// </para>
    /// <para>
    /// <b>The shared info is the part nobody guesses right.</b> The KDF is not fed the raw wrap OID; it is fed
    /// a DER-encoded <c>ECC-CMS-SharedInfo</c> holding the key-wrap algorithm, the optional user keying
    /// material, and the KEK length IN BITS as a four-byte big-endian integer (RFC 5753 §7.2). Omit the UKM
    /// when the sender sent one, or get the length field wrong, and the derived KEK is simply a different key.
    /// </para>
    /// </remarks>
    /// <param name="deriveSharedSecret">Performs the private half of the agreement over the originator's
    /// public point — the card, in production; a software key in a test.</param>
    internal static byte[] AgreeContentKey(
        byte[] raw, byte[] encryptedKey, Func<byte[], byte[]> deriveSharedSecret)
    {
        var (originatorPoint, ukm, kdfSchemeOid, wrapOid) = ReadKeyAgreement(raw);

        if (!KdfSchemes.TryGetValue(kdfSchemeOid, out var kdfHash))
        {
            throw new EnvelopeNotOpenedException(
                $"the document derives its key with a scheme this client does not implement ({kdfSchemeOid})");
        }

        var kekLength = wrapOid switch
        {
            Aes128WrapOid => 16,
            Aes192WrapOid => 24,
            Aes256WrapOid => 32,
            _ => throw new EnvelopeNotOpenedException(
                $"the document wraps its content key with an algorithm this client does not implement ({wrapOid})"),
        };

        // The card does the one thing only it can: the private half of the agreement. Everything after this is
        // managed code, which is what keeps it testable against published vectors.
        var sharedSecret = deriveSharedSecret(originatorPoint);

        var kek = KeyAgreementCrypto.DeriveKeyEncryptionKey(
            sharedSecret, EccCmsSharedInfo(wrapOid, ukm, kekLength * 8), kekLength, kdfHash);

        return KeyAgreementCrypto.UnwrapKey(kek, encryptedKey);
    }

    /// <summary>
    /// The <c>ECC-CMS-SharedInfo</c> the KDF is fed (RFC 5753 §7.2).
    /// </summary>
    /// <remarks>
    /// <code>
    /// ECC-CMS-SharedInfo ::= SEQUENCE {
    ///     keyInfo      AlgorithmIdentifier,        -- the KEY WRAP algorithm, not the content cipher
    ///     entityUInfo  [0] EXPLICIT OCTET STRING OPTIONAL,   -- the UKM, verbatim, when present
    ///     suppPubInfo  [2] EXPLICIT OCTET STRING }           -- KEK length in BITS, 4 bytes, big-endian
    /// </code>
    /// The tags are 0 and <b>2</b> — there is no [1]. Writing [1] for suppPubInfo produces a structure that
    /// encodes perfectly and derives a different key, with nothing to point at but a failed integrity check.
    /// <para>
    /// <c>keyInfo</c> carries NO parameters for an AES key wrap. An explicit NULL is the reflex from RSA
    /// algorithm identifiers and is wrong here: RFC 3565 says absent, and absent versus NULL is two different
    /// byte strings hashed into two different keys.
    /// </para>
    /// </remarks>
    internal static byte[] EccCmsSharedInfo(string keyWrapOid, byte[]? ukm, int kekLengthBits)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(keyWrapOid);
            }

            if (ukm is { Length: > 0 })
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                {
                    writer.WriteOctetString(ukm);
                }
            }

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true)))
            {
                writer.WriteOctetString(
                [
                    (byte)(kekLengthBits >> 24), (byte)(kekLengthBits >> 16),
                    (byte)(kekLengthBits >> 8), (byte)kekLengthBits,
                ]);
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// The originator's ephemeral public point, the user keying material and the key-wrap OID, read out of the
    /// CMS structure.
    /// </summary>
    /// <remarks>
    /// <code>
    /// KeyAgreeRecipientInfo ::= SEQUENCE {
    ///     version, originator [0] EXPLICIT OriginatorIdentifierOrKey,
    ///     ukm [1] EXPLICIT OCTET STRING OPTIONAL,
    ///     keyEncryptionAlgorithm AlgorithmIdentifier, recipientEncryptedKeys }
    /// -- and keyEncryptionAlgorithm NESTS the key wrap inside its parameters:
    /// --   SEQUENCE { algorithm dhSinglePass-stdDH-shaXkdf-scheme,
    /// --              parameters SEQUENCE { algorithm id-aesNNN-wrap } }
    /// OriginatorIdentifierOrKey ::= CHOICE { ..., originatorKey [1] OriginatorPublicKey }
    /// OriginatorPublicKey ::= SEQUENCE { algorithm AlgorithmIdentifier, publicKey BIT STRING }
    /// </code>
    /// <para>
    /// Read from the RAW bytes rather than from <c>KeyAgreeRecipientInfo</c>'s properties because .NET does not
    /// surface the UKM at all — and the UKM is an input to the KDF, so a recipient that carries one cannot be
    /// opened without it. The same reason <c>ReadContentCipher</c> above exists.
    /// </para>
    /// <para>
    /// The point comes out of a BIT STRING, so it is the uncompressed <c>04||X||Y</c> form directly — NOT the
    /// DER SubjectPublicKeyInfo that a PEM public key would give, which is what <c>DeriveEcdhSecret</c> would
    /// reject.
    /// </para>
    /// </remarks>
    internal static (byte[] OriginatorPoint, byte[]? Ukm, string KdfSchemeOid, string KeyWrapOid)
        ReadKeyAgreement(byte[] cms)
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
                envelopedData.ReadEncodedValue(); // originatorInfo
            }

            var recipient = envelopedData.ReadSetOf()
                .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true));

            recipient.ReadInteger(); // version, always 3 for a key-agreement recipient

            var originatorKey = recipient
                .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
                .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: false));

            originatorKey.ReadSequence();   // the ephemeral key's own algorithm identifier, not ours to check
            var point = originatorKey.ReadBitString(out _);

            byte[]? ukm = null;
            if (recipient.PeekTag() == new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true))
            {
                ukm = recipient
                    .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true))
                    .ReadOctetString();
            }

            // TWO OIDs, one nested in the other's parameters — and reading only the outer one was this
            // implementation's own bug, caught by a real OpenSSL envelope: the outer names the KDF hash, the
            // INNER names the key wrap. Taking the outer as the wrap OID makes every envelope look like it
            // used an algorithm this client does not implement.
            var keyEncryption = recipient.ReadSequence();
            var kdfScheme = keyEncryption.ReadObjectIdentifier();
            var wrap = keyEncryption.ReadSequence().ReadObjectIdentifier();

            return (point, ukm, kdfScheme, wrap);
        }
        catch (AsnContentException e)
        {
            throw new EnvelopeNotOpenedException("its key-agreement structure could not be read", e);
        }
    }

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
