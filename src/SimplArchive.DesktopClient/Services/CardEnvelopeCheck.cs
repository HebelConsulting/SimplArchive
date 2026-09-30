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
        Console.WriteLine($"envelope: {raw.Length} bytes from {envelopePath}");

        // THE KEY-AGREEMENT DETAIL IS PRINTED LATER, once the recipient is known. It used to be read here,
        // at position 0 — which throws on a mixed envelope whose first recipient is a key-transport one, and
        // threw before a single line of diagnosis had been printed (#1500).
        if (CardCertificates.FindModule() is not { } modulePath)
        {
            Console.WriteLine("REFUSED: no PKCS#11 module found.");
            return;
        }

        Console.WriteLine($"  module           : {modulePath}");

        using var library = new Pkcs11Library(new Pkcs11Options { ModulePath = modulePath });

        // THE CARD THE ENVELOPE IS ADDRESSED TO, chosen the way the production opener chooses it (#1500).
        // `library.OpenSession()` plus `keys[0]` was the shape that made this check unusable the moment a
        // SECOND card was in a reader: with a nano and a 5C inserted it logged into the wrong token, found
        // three private keys, took the first, and an ECDH derive against an RSA key threw
        // CKR_KEY_TYPE_INCONSISTENT as an unhandled exception — a crash where a diagnostic is the whole point.
        var recipients = EnvelopeRecipients.Of(raw);
        Console.WriteLine($"  addressed to     : {recipients.Count} recipient(s)");

        var onCard = CardCertificates.ReadFromLibrary(library);
        Console.WriteLine($"  certificates     : {onCard.Count} across every slot with a token");

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
            Console.WriteLine("REFUSED: no certificate on any inserted card is one of this envelope's "
                + "recipients. Cards seen: "
                + string.Join(", ", onCard.Select(c => $"{c.TokenSerial}/{c.ObjectLabel}")));
            return;
        }

        foreach (var (card, index) in agreeing)
        {
            Console.WriteLine($"  candidate        : {card.TokenSerial} → recipient #{index} "
                + $"({envelope.RecipientInfos[index].GetType().Name})");
        }

        if (agreeing.FirstOrDefault(m =>
                envelope.RecipientInfos[m.Index] is System.Security.Cryptography.Pkcs.KeyAgreeRecipientInfo)
            is not { Index: >= 0 } chosen)
        {
            Console.WriteLine("REFUSED: every matching card's recipient is key TRANSPORT (RSA), which this "
                + "check does not cover — use --card-open-test for that path.");
            return;
        }

        var addressed = chosen.Card;

        Console.WriteLine($"  matched          : {addressed.TokenSerial} / {addressed.ObjectLabel}");
        Console.WriteLine($"  subject          : {addressed.Certificate.Subject}");

        if (OpenOn(library, addressed, raw, chosen.Index) is not { } opened)
        {
            return;
        }

        Console.WriteLine($"OPENED: {opened.Length} bytes");
        Console.WriteLine($"content: {System.Text.Encoding.UTF8.GetString(opened).TrimEnd()}");
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
            Console.WriteLine($"  private keys     : {keys.Count} on this card");

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

            Console.WriteLine(index >= 0
                ? $"  key              : matched by CKA_ID at {index}"
                : "  key              : no CKA_ID match, using the first");

            var (point, ukm, kdfScheme, wrapOid) = CardEnvelopeOpener.ReadKeyAgreement(raw, mine);
            Console.WriteLine($"  originator point : {point.Length} bytes, first byte 0x{point[0]:X2}");
            Console.WriteLine($"  user keying mat. : {(ukm is null ? "(absent)" : $"{ukm.Length} bytes")}");
            Console.WriteLine($"  kdf scheme       : {kdfScheme}");
            Console.WriteLine($"  key wrap         : {wrapOid}");

            return CardEnvelopeOpener.OpenWithAgreement(
                raw, point => session.DeriveEcdhSecret(point, keys[index >= 0 ? index : 0]), mine);
        }

        Console.WriteLine($"REFUSED: the matched card ({addressed.TokenSerial}) is no longer in a reader.");
        return null;
    }
}
