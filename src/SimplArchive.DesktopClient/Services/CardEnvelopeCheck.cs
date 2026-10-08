using CAManagement.Pkcs11;
using CAManagement.Pkcs11.Configuration;
using CAManagement.Pkcs11.DataStructures;
using CAManagement.Pkcs11.Extensions;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Opens a CMS envelope with the card in the reader, and prints what happened — the verification that needs
/// hardware, invoked by <c>--card-envelope-test</c>.
/// </summary>
/// <remarks>
/// Deliberately prints each step rather than just a verdict. When a key agreement fails, the integrity check
/// is all that says so, and "the unwrap did not verify" is true of a wrong KDF hash, a wrong key-wrap OID, a
/// misread shared info and a wrong key alike — so the steps are worth seeing separately.
/// </remarks>
internal static class CardEnvelopeCheck
{
    internal static void Run(string envelopePath)
    {
        var raw = File.ReadAllBytes(envelopePath);
        DesktopLog.Info("envelope: {Bytes} bytes from {EnvelopePath}", raw.Length, envelopePath);

        // THE KEY-AGREEMENT DETAIL IS PRINTED LATER, once the recipient is known. It used to be read here,
        // at position 0 — which throws on a mixed envelope whose first recipient is a key-transport one, and
        // threw before a single line of diagnosis had been printed (#1500).
        if (CardCertificates.FindModule() is not { } modulePath)
        {
            DesktopLog.Warn("REFUSED: no PKCS#11 module found.");
            return;
        }

        DesktopLog.Info("  module           : {Module}", modulePath);

        using var library = new Pkcs11Library(new Pkcs11Options { ModulePath = modulePath });

        // THE CARD THE ENVELOPE IS ADDRESSED TO, chosen the way the production opener chooses it (#1500).
        // `library.OpenSession()` plus `keys[0]` was the shape that made this check unusable the moment a
        // SECOND card was in a reader: with a nano and a 5C inserted it logged into the wrong token, found
        // three private keys, took the first, and an ECDH derive against an RSA key threw
        // CKR_KEY_TYPE_INCONSISTENT as an unhandled exception — a crash where a diagnostic is the whole point.
        var recipients = EnvelopeRecipients.Of(raw);
        DesktopLog.Info("  addressed to     : {Recipients} recipient(s)", recipients.Count);

        var onCard = CardCertificates.ReadFromLibrary(library);
        DesktopLog.Info("  certificates     : {Certificates} across every slot with a token", onCard.Count);

        // THE CARD THIS CHECK CAN EXERCISE, which is not simply the first that matches. An envelope may
        // address a reader's whole certificate set (ADR 0842) — here a nano by key TRANSPORT and a 5C by key
        // AGREEMENT — and this check is the agreement one by design. Taking the first match made it decline
        // on a mixed envelope while the card it exists to test sat in the next reader.
        var envelope = new System.Security.Cryptography.Pkcs.EnvelopedCms();
        envelope.Decode(raw);

        var agreeing = onCard
            .Select(c => (Card: c, Index: CardEnvelopeOpener.RecipientIndexOf(envelope, c.Certificate)))
            .Where(m => m.Index >= 0)
            .ToList();

        if (agreeing.Count == 0)
        {
            DesktopLog.Warn("REFUSED: no certificate on any inserted card is one of this envelope's "
                + "recipients. Cards seen: {CardsSeen}",
                string.Join(", ", onCard.Select(c => $"{c.TokenSerial}/{c.ObjectLabel}")));
            return;
        }

        foreach (var (card, index) in agreeing)
        {
            DesktopLog.Info("  candidate        : {TokenSerial} → recipient #{Index} ({RecipientKind})",
                card.TokenSerial, index, envelope.RecipientInfos[index].GetType().Name);
        }

        if (agreeing.FirstOrDefault(m =>
                envelope.RecipientInfos[m.Index] is System.Security.Cryptography.Pkcs.KeyAgreeRecipientInfo)
            is not { Index: >= 0 } chosen)
        {
            DesktopLog.Warn("REFUSED: every matching card's recipient is key TRANSPORT (RSA), which this "
                + "check does not cover — use --card-open-test for that path.");
            return;
        }

        var addressed = chosen.Card;

        DesktopLog.Info("  matched          : {TokenSerial} / {ObjectLabel}", addressed.TokenSerial, addressed.ObjectLabel);
        DesktopLog.Info("  subject          : {Subject}", addressed.Certificate.Subject);

        if (OpenOn(library, addressed, raw, chosen.Index) is not { } opened)
        {
            return;
        }

        DesktopLog.Info("OPENED: {Bytes} bytes", opened.Length);
        // The proof that the unwrap was RIGHT, not merely that it did not throw: the digest of what came out, to compare
        // with `sha256sum` of the file the independent producer enveloped. Never the content: it would land in the
        // rolling log file, and a log holds no payload body (ADR 0626).
        DesktopLog.Info("content SHA-256: {Sha256}", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(opened)));
    }

    /// <summary>Logs into the matched card and opens the envelope with the key that certificate names.</summary>
    /// <remarks>
    /// A method of its own because a slot id is a CK_ULONG — <c>uint</c> on Windows, <c>ulong</c> elsewhere —
    /// so it cannot be named in a signature (ADR 0831); matching it has to happen where it is enumerated.
    /// </remarks>
    private static byte[]? OpenOn(Pkcs11Library library, CardCertificates.Found addressed, byte[] raw, int mine)
    {
        foreach (var slot in library.GetSlotList(tokenPresent: true))
        {
            if (library.GetTokenInfo(slot).SerialNumber.AsPkcs11String().Trim() is var serial
                && !string.Equals(serial, addressed.TokenSerial, StringComparison.Ordinal))
            {
                continue;
            }

            using var session = library.OpenSession(slot, readWrite: false);
            using var login = session.Login(Environment.GetEnvironmentVariable("CARD_PIN") ?? string.Empty);

            var keys = session.FindObjects(CK_OBJECT_CLASS.CKO_PRIVATE_KEY);
            DesktopLog.Info("  private keys     : {Keys} on this card", keys.Count);

            // BY CKA_ID, not the first — a PIV token has four key slots, and on the nano the first is not the
            // key-management one. The certificate names its key; ask it.
            var index = addressed.KeyId is { Length: > 0 } keyId
                ? keys.ToList().FindIndex(handle =>
                {
                    try
                    {
                        return session.GetAttributeValue(handle, CK_ATTRIBUTE_TYPE.CKA_ID)
                            .AsSpan().SequenceEqual(keyId);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                })
                : -1;

            if (index >= 0)
            {
                DesktopLog.Info("  key              : matched by CKA_ID at {Index}", index);
            }
            else
            {
                DesktopLog.Info("  key              : no CKA_ID match, using the first");
            }

            var (point, ukm, kdfScheme, wrapOid) = CardEnvelopeOpener.ReadKeyAgreement(raw, mine);
            DesktopLog.Info("  originator point : {Bytes} bytes, first byte 0x{FirstByte:X2}", point.Length, point[0]);
            DesktopLog.Info("  user keying mat. : {UserKeyingMaterial}", ukm is null ? "(absent)" : $"{ukm.Length} bytes");
            DesktopLog.Info("  kdf scheme       : {KdfScheme}", kdfScheme);
            DesktopLog.Info("  key wrap         : {KeyWrap}", wrapOid);

            return CardEnvelopeOpener.OpenWithAgreement(
                raw, point => session.DeriveEcdhSecret(point, keys[index >= 0 ? index : 0]), mine);
        }

        DesktopLog.Warn("REFUSED: the matched card ({TokenSerial}) is no longer in a reader.", addressed.TokenSerial);
        return null;
    }
}
